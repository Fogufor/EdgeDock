using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows.Threading;
using EdgeDock.Core;

namespace EdgeDock.Modules.Media;

/// <summary>
/// Музыка из Яндекс Браузера. Он не сообщает Windows о воспроизведении, поэтому трек присылает
/// наше расширение (папка BrowserExtension) по WebSocket на 127.0.0.1. Пускаем только это расширение:
/// браузер подставляет его адрес в заголовок Origin, и подделать его страница не может.
/// В простое ничего не делает — только ждёт подключения.
/// </summary>
internal sealed class BrowserMusicBridge : IDisposable
{
    /// <summary>Порт; должен совпадать с WIDGET_URL в BrowserExtension/background.js.</summary>
    public const int Port = 48620;

    /// <summary>ID расширения; задан ключом "key" в BrowserExtension/manifest.json.</summary>
    private const string ExtensionId = "bihfedgclefgkpmnhbocgepnjkidjlki";

    private const int MaxHeaderBytes = 8 * 1024;
    private const int MaxMessageBytes = 512 * 1024; // обложка может прийти data:-адресом
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(5);

    public sealed record Track(string Title, string Artist, string Artwork, bool Playing,
        bool CanPrevious, bool CanPlayPause, bool CanNext);

    private readonly Dispatcher _ui;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private TcpListener? _listener;
    private CancellationTokenSource? _stop;
    private WebSocket? _socket;

    /// <summary>Трек изменился; null — в браузере ничего не играет или расширение отключилось. В потоке интерфейса.</summary>
    public event Action<Track?>? Changed;

    public BrowserMusicBridge(Dispatcher ui) => _ui = ui;

    public void Start()
    {
        if (_listener != null) return;
        try
        {
            _listener = new TcpListener(IPAddress.Loopback, Port);
            _listener.Start();
        }
        catch (SocketException ex)
        {
            Log.Error($"Музыка из браузера: порт {Port} занят другой программой.", ex);
            _listener = null;
            return;
        }
        _stop = new CancellationTokenSource();
        _ = AcceptLoop(_listener, _stop.Token);
    }

    public void Stop()
    {
        _stop?.Cancel();
        _stop = null;
        _listener?.Stop();
        _listener = null;
        Interlocked.Exchange(ref _socket, null)?.Abort();
        Raise(null);
    }

    /// <summary>Кнопка из виджета: "previous", "playPause" или "next".</summary>
    public async void Send(string command)
    {
        var socket = _socket;
        if (socket?.State != WebSocketState.Open) return;
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(new { type = "command", command });
        await _sendLock.WaitAsync();
        try
        {
            await socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);
        }
        catch (Exception ex) when (ex is WebSocketException or IOException or ObjectDisposedException)
        {
            // Соединение закрылось — расширение переподключится само.
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private async Task AcceptLoop(TcpListener listener, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(token);
            }
            catch (Exception) when (token.IsCancellationRequested)
            {
                return;
            }
            catch (SocketException ex)
            {
                Log.Error("Музыка из браузера: не удалось принять подключение.", ex);
                continue;
            }
            _ = Serve(client, token);
        }
    }

    private async Task Serve(TcpClient client, CancellationToken token)
    {
        WebSocket? socket = null;
        using (client)
        {
            try
            {
                socket = await Handshake(client.GetStream(), token);
                if (socket == null) return;

                // Подключение одно: новое (например, после перезапуска браузера) заменяет старое.
                Interlocked.Exchange(ref _socket, socket)?.Abort();
                await ReceiveLoop(socket, token);
            }
            catch (Exception ex) when (ex is IOException or WebSocketException or OperationCanceledException or ObjectDisposedException)
            {
                // Браузер закрыли или соединение оборвалось — это нормально.
            }
            finally
            {
                if (socket != null && Interlocked.CompareExchange(ref _socket, null, socket) == socket) Raise(null);
                socket?.Dispose();
            }
        }
    }

    /// <summary>Принять HTTP-запрос на переход к WebSocket, но только от нашего расширения.</summary>
    private static async Task<WebSocket?> Handshake(NetworkStream stream, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(HandshakeTimeout);

        var buffer = new byte[MaxHeaderBytes];
        int length = 0;
        string head;
        while (true)
        {
            if (length == buffer.Length) return null;
            int read = await stream.ReadAsync(buffer.AsMemory(length), timeout.Token);
            if (read == 0) return null;
            length += read;
            string text = Encoding.ASCII.GetString(buffer, 0, length);
            int end = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            if (end < 0) continue;
            head = text[..end];
            break;
        }

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string line in head.Split("\r\n").Skip(1))
        {
            int colon = line.IndexOf(':');
            if (colon > 0) headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
        }

        headers.TryGetValue("Origin", out string? origin);
        if (!headers.TryGetValue("Sec-WebSocket-Key", out string? key) || !IsOurExtension(origin))
        {
            await stream.WriteAsync("HTTP/1.1 403 Forbidden\r\nContent-Length: 0\r\n\r\n"u8.ToArray(), timeout.Token);
            return null;
        }

        string accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
        string response = "HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n" +
                          $"Sec-WebSocket-Accept: {accept}\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(response), timeout.Token);

        // Без пингов: соединение ничего не делает, пока расширению нечего сообщить.
        return WebSocket.CreateFromStream(stream, new WebSocketCreationOptions { IsServer = true, KeepAliveInterval = TimeSpan.Zero });
    }

    // Яндекс Браузер пишет chrome-extension://<id>; схему проверяем мягко, ID — строго.
    private static bool IsOurExtension(string? origin) =>
        origin != null && origin.EndsWith("-extension://" + ExtensionId, StringComparison.OrdinalIgnoreCase);

    private async Task ReceiveLoop(WebSocket socket, CancellationToken token)
    {
        var buffer = new byte[MaxMessageBytes];
        while (socket.State == WebSocketState.Open)
        {
            int length = 0;
            ValueWebSocketReceiveResult result;
            do
            {
                if (length == buffer.Length)
                {
                    await socket.CloseAsync(WebSocketCloseStatus.MessageTooBig, null, token);
                    return;
                }
                result = await socket.ReceiveAsync(buffer.AsMemory(length), token);
                if (result.MessageType == WebSocketMessageType.Close) return;
                length += result.Count;
            } while (!result.EndOfMessage);

            Handle(Encoding.UTF8.GetString(buffer, 0, length));
        }
    }

    private void Handle(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            switch (Text(root, "type"))
            {
                case "none":
                    Raise(null);
                    break;
                case "state":
                    var track = new Track(Text(root, "title"), Text(root, "artist"), Text(root, "artwork"),
                        Flag(root, "playing"), Flag(root, "canPrevious"), Flag(root, "canPlayPause"), Flag(root, "canNext"));
                    Raise(track.Title.Length > 0 ? track : null);
                    break;
            }
        }
        catch (JsonException)
        {
            // Непонятное сообщение просто пропускаем.
        }
    }

    private static string Text(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";

    private static bool Flag(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private void Raise(Track? track) => _ui.BeginInvoke(() => Changed?.Invoke(track));

    public void Dispose()
    {
        Stop();
        _sendLock.Dispose();
    }
}
