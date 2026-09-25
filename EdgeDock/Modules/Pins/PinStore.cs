using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using EdgeDock.Core;

namespace EdgeDock.Modules.Pins;

/// <summary>Элемент в pins.json.</summary>
internal sealed class PinEntry
{
    public string Label { get; set; } = "";
    public string Value { get; set; } = "";
}

/// <summary>
/// pins.json — то, что закрепил пользователь. Пишет только виджет, через временный файл.
/// Файл не читается — остаётся как есть, а запись отключается, чтобы не стереть данные.
/// </summary>
internal sealed class PinStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // кириллица в файле остаётся читаемой
    };

    private readonly string _path;

    public PinStore(string path) => _path = path;

    /// <summary>Файл прочитан (или его ещё нет) — можно сохранять.</summary>
    public bool IsReadable { get; private set; } = true;

    public List<PinEntry> Load()
    {
        try
        {
            if (!File.Exists(_path)) return [];
            var entries = JsonSerializer.Deserialize<List<PinEntry>>(File.ReadAllText(_path), Json) ?? [];
            return entries.Where(e => e != null && !string.IsNullOrWhiteSpace(e.Label) && !string.IsNullOrWhiteSpace(e.Value)).ToList();
        }
        catch (Exception ex)
        {
            Log.Error("pins.json: не удалось прочитать, файл оставлен как есть.", ex);
            IsReadable = false;
            return [];
        }
    }

    /// <summary>Сохранить. false — не получилось, или файл не читался и трогать его нельзя.</summary>
    public bool Save(IEnumerable<PinEntry> entries)
    {
        if (!IsReadable) return false;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            string temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(entries.ToList(), Json));
            File.Move(temp, _path, overwrite: true);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("pins.json: не удалось сохранить.", ex);
            return false;
        }
    }
}
