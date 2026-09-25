namespace EdgeDock.Modules.Pins;

/// <summary>Что лежит под названием — по нему выбирается значок плашки.</summary>
public enum PinKind { Link, Tag, Phone, Text }

internal static class PinKinds
{
    /// <summary>
    /// Ссылка — http://, https:// или www.; тег — # или @ в начале; телефон — только цифры, +, пробелы,
    /// скобки и дефисы, цифр не меньше пяти; всё остальное — текст.
    /// </summary>
    public static PinKind Detect(string value)
    {
        string v = value.Trim();
        if (v.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            v.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
            v.StartsWith("www.", StringComparison.OrdinalIgnoreCase))
            return PinKind.Link;
        if (v.StartsWith('#') || v.StartsWith('@')) return PinKind.Tag;
        if (v.All(c => char.IsAsciiDigit(c) || c is '+' or ' ' or '(' or ')' or '-') && v.Count(char.IsAsciiDigit) >= 5)
            return PinKind.Phone;
        return PinKind.Text;
    }
}
