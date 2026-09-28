# Автообновление — план реализации

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** виджет сам находит новую версию на GitHub, проверяет её и ставит без окна и без предупреждения Windows.

**Architecture:** `Updater` (Setup) — чистая логика: запрос последнего релиза, сравнение версий, скачивание с подсчётом SHA-256, сверка с `digest` ассета и с версией внутри exe; результат — `UpdateCheck`. `UpdateService` — когда проверять (через 2 мин после запуска, по пункту трея) и когда ставить (док «тих»). Скачанный exe запускается с `--update` и вызывает `Installer.Update()` — тот же `Install`, что у окна установки.

**Tech Stack:** C#, .NET 10, `HttpClient`, `System.Text.Json`, `IncrementalHash`, Shell_NotifyIcon (уведомления).

**Spec:** `SPEC.md` → «Обновления».

## Global Constraints

* Никакого постоянного опроса: одна проверка после запуска и по пункту трея; таймер повторяет только попытку поставить уже скачанное (раз в 30 с), пока док занят.
* Только https; сумма `sha256:` из ассета обязательна; `ProductName == "EdgeDock"` и `FileVersion` = версия релиза; не новее — не ставим.
* Сети нет — автоматическая проверка молчит (в лог не пишет); ошибки суммы и версии — в лог.
* Новых зависимостей нет. Код и комментарии — на русском.
* Коммит после этапа; сообщение заканчивается строкой `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.

## Файлы

* Создать `EdgeDock/Setup/Updater.cs` — `UpdateStatus`, `UpdateCheck`, `Updater`.
* Создать `EdgeDock/Setup/UpdateService.cs`.
* Изменить `EdgeDock/Setup/Installer.cs` (`Update()`), `EdgeDock/App.xaml.cs` (режим `--update`, сервис, пункт трея), `EdgeDock/Core/TrayIcon.cs` (`CheckUpdates`, `ShowNotice`), `EdgeDock/Core/Native.cs` (`NIF_INFO`, `NIIF_INFO`), `EdgeDock/Core/AppPaths.cs` (`Updates`), `EdgeDock/Core/SettingsService.cs` (`AutoUpdate`), `EdgeDock/DockWindow.xaml.cs` (`IsQuiet`), `README.md`, `EdgeDock/EdgeDock.csproj` (1.8.0).
* Тест (scratchpad): `update/updatetest.cs` — файловое приложение .NET, вызывает `Updater.FetchNewerAsync` из собранного `EdgeDock.dll` с поддельным GitHub.

---

### Task 1: Updater (логика) — тест первым

**Interfaces — Produces:**
`internal enum UpdateStatus { UpToDate, Ready, Failed }`;
`internal sealed record UpdateCheck(UpdateStatus Status, Version? Version = null, string? Path = null, string? Error = null, bool Offline = false)`;
`Updater.LatestRelease : Uri`, `Updater.UpdateArgument = "--update"`,
`Updater.FetchNewerAsync(HttpClient http, Uri api, Version current, string folder, CancellationToken token = default) : Task<UpdateCheck>`,
`Updater.Apply(string path)`, `Updater.DeleteDownloads(string folder)`.

- [ ] **Step 1: тест** `update/updatetest.cs`:

```csharp
#:property TargetFramework=net10.0-windows
#:property UseWPF=true
#:property PublishAot=false
#:property Nullable=enable

// Логика автообновления EdgeDock с поддельным GitHub: вызывает настоящий Updater.FetchNewerAsync из EdgeDock.dll.
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

string dll = args[0], exe = args[1];   // EdgeDock.dll и EdgeDock.exe той же сборки (exe — «скачиваемый» файл)
var assembly = Assembly.LoadFrom(dll);
var updater = assembly.GetType("EdgeDock.Setup.Updater", throwOnError: true)!;
var fetch = updater.GetMethod("FetchNewerAsync", BindingFlags.Public | BindingFlags.Static)!;

byte[] exeBytes = File.ReadAllBytes(exe);
string sha = Convert.ToHexString(SHA256.HashData(exeBytes)).ToLowerInvariant();
var fileVersion = Version.Parse(FileVersionInfo.GetVersionInfo(exe).FileVersion!);
var release = new Version(fileVersion.Major, fileVersion.Minor, fileVersion.Build);
var older = new Version(release.Major, release.Minor, Math.Max(0, release.Build - 1));
if (release.Build == 0) older = new Version(release.Major, Math.Max(0, release.Minor - 1), 9);
string folder = Path.Combine(Path.GetTempPath(), "edgedock-updatetest-" + Guid.NewGuid().ToString("N"));
var api = new Uri("https://api.github.com/repos/Fogufor/EdgeDock/releases/latest");
int failures = 0;

async Task<(string Status, string? Path, string? Error, bool Offline, int Downloads)> Run(string tag, string url, string? digest, byte[] body, Version current, bool offline = false)
{
    var handler = new FakeGitHub(tag, url, digest, body, offline);
    var task = (Task)fetch.Invoke(null, [new HttpClient(handler), api, current, folder, CancellationToken.None])!;
    await task;
    var result = task.GetType().GetProperty("Result")!.GetValue(task)!; // UpdateCheck — внутренний тип EdgeDock
    var type = result.GetType();
    return (type.GetProperty("Status")!.GetValue(result)!.ToString()!, (string?)type.GetProperty("Path")!.GetValue(result),
        (string?)type.GetProperty("Error")!.GetValue(result), (bool)type.GetProperty("Offline")!.GetValue(result)!, handler.Downloads);
}
void Check(string name, bool ok, object? info = null) { if (!ok) failures++; Console.WriteLine($"{(ok ? "PASS" : "FAIL")} {name}{(info != null ? $"  [{info}]" : "")}"); }
string Url = "https://github.com/Fogufor/EdgeDock/releases/download/v1/EdgeDock.exe";

// 1. Новее, сумма верная, внутри нужная версия — готово к установке.
var r = await Run($"v{release}", Url, "sha256:" + sha, exeBytes, older);
Check("newer release → ready", r.Status == "Ready" && r.Path != null && File.Exists(r.Path), r.Status + " " + r.Error);
Check("downloaded file is exact copy", r.Path != null && File.Exists(r.Path) && File.ReadAllBytes(r.Path).AsSpan().SequenceEqual(exeBytes));
Check("no .part left", !Directory.Exists(folder) || Directory.GetFiles(folder, "*.part").Length == 0);

// 2. Та же версия — обновлять нечего, файл даже не скачиваем.
r = await Run($"v{release}", Url, "sha256:" + sha, exeBytes, release);
Check("same version → up to date, no download", r.Status == "UpToDate" && r.Downloads == 0, $"{r.Status}, downloads={r.Downloads}");

// 3. Подменённый файл: сумма не совпала — не ставим и не оставляем.
Directory.Delete(folder, true);
r = await Run($"v{release}", Url, "sha256:" + sha, Encoding.ASCII.GetBytes("not the real exe"), older);
Check("checksum mismatch → failed, nothing left", r.Status == "Failed" && (!Directory.Exists(folder) || Directory.GetFiles(folder).Length == 0), r.Error);

// 4. GitHub не сообщил сумму — не ставим.
r = await Run($"v{release}", Url, null, exeBytes, older);
Check("no digest → failed", r.Status == "Failed", r.Error);

// 5. Сумма верная, но внутри другая версия (релиз «v99.0.0» с файлом текущей версии) — не ставим.
r = await Run("v99.0.0", Url, "sha256:" + sha, exeBytes, older);
Check("version inside file differs → failed", r.Status == "Failed" && (!Directory.Exists(folder) || Directory.GetFiles(folder).Length == 0), r.Error);

// 6. Адрес загрузки не https — не качаем.
r = await Run($"v{release}", "http://example.com/EdgeDock.exe", "sha256:" + sha, exeBytes, older);
Check("http download url → failed, no download", r.Status == "Failed" && r.Downloads == 0, r.Error);

// 7. Сети нет — «не получилось», помечено как офлайн, без исключения.
r = await Run($"v{release}", Url, "sha256:" + sha, exeBytes, older, offline: true);
Check("offline → failed quietly", r.Status == "Failed" && r.Offline, r.Error);

if (Directory.Exists(folder)) Directory.Delete(folder, true);
Console.WriteLine(failures == 0 ? "all passed" : $"{failures} failed");
return failures == 0 ? 0 : 1;

// Поддельный GitHub: ответ API о релизе и сам файл.
sealed class FakeGitHub(string tag, string url, string? digest, byte[] body, bool offline) : HttpMessageHandler
{
    public int Downloads;
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        if (offline) throw new HttpRequestException("Нет подключения к сети.");
        if (request.RequestUri!.Host == "api.github.com")
        {
            string asset = $"{{\"name\":\"EdgeDock.exe\",\"browser_download_url\":\"{url}\"{(digest != null ? $",\"digest\":\"{digest}\"" : "")}}}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent($"{{\"tag_name\":\"{tag}\",\"assets\":[{asset}]}}") });
        }
        Downloads++;
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) });
    }
}
```

- [ ] **Step 2:** `dotnet build` и `dotnet run --file update/updatetest.cs -- <bin>\EdgeDock.dll <bin>\EdgeDock.exe` — ожидается исключение «тип EdgeDock.Setup.Updater не найден».

- [ ] **Step 3: реализация** `EdgeDock/Setup/Updater.cs`:

```csharp
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
```

- [ ] **Step 4:** тест — `all passed`.

---

### Task 2: Установка по `--update`, сервис, трей

- [ ] **`Installer.Update()`** (после `Install`):

```csharp
    /// <summary>Тихое обновление (exe запустил виджет с --update): в ту же папку, с теми же галочками.</summary>
    public static void Update()
    {
        string folder = InstalledFolder() ?? throw new InvalidOperationException("EdgeDock не установлен — обновлять нечего.");
        var settings = new SettingsService();
        settings.LoadSettings();
        Install(folder, settings.Settings.Dock.Autostart, startMenu: File.Exists(StartMenuShortcut));
    }
```

- [ ] **`App.OnStartup`** — перед проверкой `ShouldShowSetup`:

```csharp
        // Тихое обновление: этот exe скачал работающий виджет (см. Updater) — поставить себя и закрыться.
        if (e.Args.Contains(Updater.UpdateArgument, StringComparer.OrdinalIgnoreCase))
        {
            Task.Run(() =>
            {
                try
                {
                    Installer.Update();
                }
                catch (Exception ex)
                {
                    Log.Error("Обновление: не удалось установить новую версию.", ex);
                }
            }).ContinueWith(_ => Dispatcher.BeginInvoke(() => Shutdown()));
            return;
        }
```

после создания трея и дока:

```csharp
        _updates = new UpdateService(() => _dock.IsQuiet, (title, text) => _tray?.ShowNotice(title, text));
        if (_settings.Settings.Dock.AutoUpdate) _updates.ScheduleFirstCheck();
```

в `OnTrayCommand`: `case TrayCommand.CheckUpdates: _updates?.CheckNow(manual: true); break;`; в `OnExit`: `_updates?.Dispose();`; поле `private UpdateService? _updates;`.

- [ ] **`UpdateService.cs`**:

```csharp
using System.Net.Http;
using System.Windows.Threading;
using EdgeDock.Core;

namespace EdgeDock.Setup;

/// <summary>
/// Когда проверять обновление и когда ставить (сама проверка — Updater). Проверка — один раз через 2 минуты после
/// запуска и по пункту трея; постоянного опроса нет. Ставить — только когда док «тих»: панель свернута, ничего не на
/// весь экран; иначе повторить попытку через 30 с.
/// </summary>
internal sealed class UpdateService : IDisposable
{
    private static readonly TimeSpan FirstCheckDelay = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan ApplyRetry = TimeSpan.FromSeconds(30);
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(10) };

    private readonly DispatcherTimer _timer = new();
    private readonly Func<bool> _canRestart;
    private readonly Action<string, string> _notify;
    private bool _checking;
    private UpdateCheck? _ready;

    public UpdateService(Func<bool> canRestart, Action<string, string> notify)
    {
        _canRestart = canRestart;
        _notify = notify;
        _timer.Tick += (_, _) => OnTimer();
    }

    public void ScheduleFirstCheck()
    {
        _timer.Interval = FirstCheckDelay;
        _timer.Start();
    }

    /// <summary>Проверить сейчас. manual — по пункту трея: ответить уведомлением в любом случае.</summary>
    public async void CheckNow(bool manual)
    {
        if (_ready != null)
        {
            TryApply();
            return;
        }
        if (_checking) return;
        _checking = true;
        try
        {
            Updater.DeleteDownloads(AppPaths.Updates);
            var result = await Updater.FetchNewerAsync(Http, Updater.LatestRelease, Version.Parse(Installer.Version), AppPaths.Updates);
            switch (result.Status)
            {
                case UpdateStatus.UpToDate:
                    if (manual) _notify("EdgeDock", $"Установлена последняя версия {Installer.Version}.");
                    break;
                case UpdateStatus.Failed:
                    if (!result.Offline) Log.Warn($"Обновление: {result.Error}");
                    if (manual) _notify("Не удалось проверить обновления", result.Error ?? "");
                    break;
                case UpdateStatus.Ready:
                    _ready = result;
                    TryApply();
                    break;
            }
        }
        finally
        {
            _checking = false;
        }
    }

    private void OnTimer()
    {
        _timer.Stop();
        if (_ready != null) TryApply();
        else CheckNow(manual: false);
    }

    private void TryApply()
    {
        if (_ready?.Path is not string path) return;
        if (!_canRestart())
        {
            _timer.Interval = ApplyRetry;
            _timer.Start();
            return;
        }
        _notify("EdgeDock обновляется", $"Версия {_ready.Version}: виджет перезапустится на пару секунд.");
        try
        {
            Updater.Apply(path);
        }
        catch (Exception ex)
        {
            Log.Error("Обновление: не удалось запустить установку.", ex);
            _ready = null;
        }
    }

    public void Dispose() => _timer.Stop();
}
```

- [ ] **Трей**: `TrayCommand` + `CheckUpdates` (в конец перечисления); пункт «Проверить обновления» после «Перезагрузить настройки»; метод

```csharp
    /// <summary>Уведомление Windows от значка в трее (например, об обновлении).</summary>
    public void ShowNotice(string title, string text)
    {
        var data = NewData(Native.NIF_INFO);
        data.szInfoTitle = title;
        data.szInfo = text;
        data.dwInfoFlags = Native.NIIF_INFO;
        Native.Shell_NotifyIcon(Native.NIM_MODIFY, ref data);
    }
```

и в `Native`: `public const int NIF_INFO = 0x10, NIIF_INFO = 0x1;`.

- [ ] **Прочее**: `AppPaths.Updates = Root\update`; `DockSettings.AutoUpdate = true` (комментарий: «Сам проверять и ставить обновления с GitHub»); `DockWindow.IsQuiet => !_expanded && !_fullscreen && !_dragging && !_pressed;`.

- [ ] **Проверка**: сборка без предупреждений; тест Task 1; тест `--update` вручную: скопировать собранный exe в `%LocalAppData%\EdgeDock\update\EdgeDock-x.exe`, запустить с `--update` — установленный виджет перезапущен новой версией, install.json обновлён, автозапуск и ярлык «Пуска» те же.

---

### Task 3: Выпуск и настоящая проверка

- [ ] README: раздел «Обновление» — «приходят сами; в трее — «Проверить обновления»; отключить — `dock.autoUpdate: false`».
- [ ] Версия 1.8.0, коммит, push, тег, установка у пользователя.
- [ ] Затем 1.8.1 (только версия) на GitHub; перезапустить установленный 1.8.0 и дождаться (≤3 мин), что он сам стал 1.8.1: процесс новой версии, `install.json`, лог пуст.
