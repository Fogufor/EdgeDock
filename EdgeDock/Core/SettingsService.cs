using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EdgeDock.Core;

/// <summary>settings.json → "dock". Правится руками.</summary>
public sealed class DockSettings
{
    public int SnapDistancePx { get; set; } = 24;
    public double Width { get; set; } = 360;
    public int ExpandDelayMs { get; set; } = 300;
    public int CollapseDelayMs { get; set; } = 500;
    public bool Autostart { get; set; } = true;
}

/// <summary>settings.json → "pocket".</summary>
public sealed class PocketSettings
{
    /// <summary>Папка снимков. null — системная папка «Снимки экрана».</summary>
    public string? ScreenshotsFolder { get; set; }

    /// <summary>Убирать элемент с полки после того, как его перетащили наружу.</summary>
    public bool ShelfRemoveAfterDrag { get; set; }
}

/// <summary>settings.json → "audio". Устройства ищутся по вхождению названия, без учёта регистра.</summary>
public sealed class AudioSettings
{
    public List<string> FavoriteOutputs { get; set; } = [];
    public List<string> FavoriteInputs { get; set; } = [];

    /// <summary>Куда переключаться в режиме созвона. null — кнопки режима нет.</summary>
    public CallModeSettings? CallMode { get; set; }
}

public sealed class CallModeSettings
{
    public string? Output { get; set; }
    public string? Input { get; set; }
}

/// <summary>settings.json. Правится руками; сам виджет в него не пишет (кроме создания при первом запуске).</summary>
public sealed class Settings
{
    public DockSettings Dock { get; set; } = new();

    /// <summary>Модули в порядке панели. Модуль, которого нет в списке, не загружается вообще.</summary>
    public List<string> Modules { get; set; } = ["meeting", "pocket", "media", "audio", "pins"];

    public PocketSettings Pocket { get; set; } = new();

    public AudioSettings Audio { get; set; } = new();
}

/// <summary>Элемент полки в state.json.</summary>
public sealed class ShelfEntry
{
    public string Path { get; set; } = "";

    /// <summary>Файл — наша копия в %LocalAppData%\EdgeDock\shelf (у данных не было пути на диске).</summary>
    public bool Owned { get; set; }
}

/// <summary>state.json. Пишет только виджет: положение, закрепление, содержимое полки.</summary>
public sealed class AppState
{
    public DockState Dock { get; set; } = new();
    public List<ShelfEntry> Shelf { get; set; } = [];
}

/// <summary>Чтение и запись settings.json и state.json в %LocalAppData%\EdgeDock.</summary>
public sealed class SettingsService
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // кириллица в файле остаётся читаемой
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public Settings Settings { get; private set; } = new();
    public AppState State { get; private set; } = new();

    /// <summary>state.json прочитан без ошибок (или его ещё нет). Иначе чистить «ничейные» файлы полки нельзя.</summary>
    public bool StateIsTrusted { get; private set; } = true;

    /// <summary>
    /// Читает settings.json (при первом запуске создаёт его со значениями по умолчанию).
    /// Если файл испорчен — оставляет прежние настройки, пишет ошибку в лог и возвращает false.
    /// </summary>
    public bool LoadSettings()
    {
        try
        {
            if (!File.Exists(AppPaths.SettingsFile))
            {
                Settings = new Settings();
                WriteFile(AppPaths.SettingsFile, Settings);
                return true;
            }

            var loaded = JsonSerializer.Deserialize<Settings>(File.ReadAllText(AppPaths.SettingsFile), Json)
                         ?? throw new JsonException("Файл пустой.");
            Settings = Sanitize(loaded);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("settings.json: не удалось прочитать, остаются прежние настройки.", ex);
            return false;
        }
    }

    public void LoadState()
    {
        try
        {
            if (File.Exists(AppPaths.StateFile))
                State = JsonSerializer.Deserialize<AppState>(File.ReadAllText(AppPaths.StateFile), Json) ?? new AppState();
            State.Dock ??= new DockState();
            State.Shelf ??= [];
        }
        catch (Exception ex)
        {
            Log.Error("state.json: не удалось прочитать, положение и полка сброшены.", ex);
            State = new AppState();
            StateIsTrusted = false;
        }
    }

    public void SaveState()
    {
        try
        {
            WriteFile(AppPaths.StateFile, State);
        }
        catch (Exception ex)
        {
            Log.Error("state.json: не удалось сохранить.", ex);
        }
    }

    private static Settings Sanitize(Settings s)
    {
        s.Dock ??= new DockSettings();
        s.Modules ??= [];
        s.Pocket ??= new PocketSettings();
        s.Audio ??= new AudioSettings();
        s.Audio.FavoriteOutputs ??= [];
        s.Audio.FavoriteInputs ??= [];
        var d = s.Dock;
        d.SnapDistancePx = Math.Clamp(d.SnapDistancePx, 0, 200);
        d.Width = Math.Clamp(d.Width, 240, 800);
        d.ExpandDelayMs = Math.Clamp(d.ExpandDelayMs, 0, 5000);
        d.CollapseDelayMs = Math.Clamp(d.CollapseDelayMs, 0, 5000);
        return s;
    }

    /// <summary>Пишет через временный файл, чтобы сбой посреди записи не оставил полфайла.</summary>
    private static void WriteFile<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(value, Json));
        File.Move(temp, path, overwrite: true);
    }
}
