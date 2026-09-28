using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;

namespace EdgeDock.Setup;

internal enum UpdateStatus { UpToDate, Ready, Failed }

/// <summary>Итог проверки: обновлять нечего; готов проверенный файл (Path); не получилось (Error, Offline — не было сети).</summary>
internal sealed record UpdateCheck(UpdateStatus Status, Version? Version = null, string? Path = null, string? Error = null, bool Offline = false);

/// <summary>
/// Автообновление. Последний релиз на GitHub новее установленной версии — скачать EdgeDock.exe, сверить SHA-256 с тем,
/// что публикует GitHub, и версию внутри файла, затем поставить его тихо (--update) тем же путём, что и окно установки.
/// Файл скачивает сам виджет — без «метки интернета», поэтому SmartScreen не вмешивается (как у Chrome и Discord);
/// безопасность держится на HTTPS до GitHub и этих проверках.
/// </summary>
internal static class Updater
{
    public static readonly Uri LatestRelease = new("https://api.github.com/repos/Fogufor/EdgeDock/releases/latest");
    public const string UpdateArgument = "--update";
    private const string AssetName = "EdgeDock.exe";
    private const string DigestPrefix = "sha256:";

    public static async Task<UpdateCheck> FetchNewerAsync(HttpClient http, Uri api, Version current, string folder, CancellationToken token = default)
    {
        string? part = null;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, api);
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            request.Headers.UserAgent.ParseAdd($"EdgeDock/{current}");
            using var response = await http.SendAsync(request, token);
            if (!response.IsSuccessStatusCode) return Failed($"GitHub ответил {(int)response.StatusCode}.");

            using var release = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
            string tag = release.RootElement.TryGetProperty("tag_name", out var name) ? name.GetString() ?? "" : "";
            if (!Version.TryParse(tag.TrimStart('v', 'V'), out var latest)) return Failed($"Непонятная версия релиза: «{tag}».");
            if (Normalize(latest) <= Normalize(current)) return new UpdateCheck(UpdateStatus.UpToDate, latest);

            var asset = FindAsset(release.RootElement);
            if (asset is null) return Failed("В релизе нет EdgeDock.exe.");
            var (url, digest) = asset.Value;
            if (url.Scheme != Uri.UriSchemeHttps) return Failed("Адрес загрузки не https.");
            if (!digest.StartsWith(DigestPrefix, StringComparison.OrdinalIgnoreCase)) return Failed("GitHub не сообщил контрольную сумму файла.");

            Directory.CreateDirectory(folder);
            string path = Path.Combine(folder, $"EdgeDock-{Normalize(latest)}.exe");
            part = path + ".part";
            string actual;
            using (var download = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token))
            {
                download.EnsureSuccessStatusCode();
                await using var source = await download.Content.ReadAsStreamAsync(token);
                await using var file = File.Create(part);
                using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[81920];
                int read;
                while ((read = await source.ReadAsync(buffer, token)) > 0)
                {
                    sha.AppendData(buffer, 0, read);
                    await file.WriteAsync(buffer.AsMemory(0, read), token);
                }
                actual = Convert.ToHexString(sha.GetHashAndReset());
            }
            if (!string.Equals(actual, digest[DigestPrefix.Length..], StringComparison.OrdinalIgnoreCase))
                return Failed("Контрольная сумма скачанного файла не совпала с опубликованной.");

            File.Move(part, path, overwrite: true);
            part = null;

            // Сумма совпала — но внутри должна быть именно эта версия EdgeDock.
            var info = FileVersionInfo.GetVersionInfo(path);
            if (info.ProductName != "EdgeDock" || !Version.TryParse(info.FileVersion, out var inside) || Normalize(inside) != Normalize(latest))
            {
                File.Delete(path);
                return Failed($"В скачанном файле не EdgeDock {Normalize(latest)}.");
            }
            return new UpdateCheck(UpdateStatus.Ready, Normalize(latest), path);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return new UpdateCheck(UpdateStatus.Failed, Error: ex.Message, Offline: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return Failed(ex.Message);
        }
        finally
        {
            if (part != null) TryDelete(part);
        }
    }

    /// <summary>Запустить скачанный exe: он тихо поставит себя вместо работающего виджета и запустит новый.</summary>
    public static void Apply(string path) =>
        Process.Start(new ProcessStartInfo(path, UpdateArgument) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(path) });

    /// <summary>Убрать файлы, скачанные прошлыми проверками.</summary>
    public static void DeleteDownloads(string folder)
    {
        if (!Directory.Exists(folder)) return;
        foreach (string file in Directory.GetFiles(folder)) TryDelete(file);
    }

    private static (Uri Url, string Digest)? FindAsset(JsonElement release)
    {
        if (!release.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array) return null;
        foreach (var asset in assets.EnumerateArray())
        {
            if (!asset.TryGetProperty("name", out var name) || name.GetString() != AssetName) continue;
            if (!asset.TryGetProperty("browser_download_url", out var url) || !Uri.TryCreate(url.GetString(), UriKind.Absolute, out var uri)) continue;
            string digest = asset.TryGetProperty("digest", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() ?? "" : "";
            return (uri, digest);
        }
        return null;
    }

    private static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0));

    private static UpdateCheck Failed(string error) => new(UpdateStatus.Failed, Error: error);

    private static void TryDelete(string file)
    {
        try
        {
            File.Delete(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Занят — уберём в следующий раз.
        }
    }
}
