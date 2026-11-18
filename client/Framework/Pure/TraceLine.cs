using System;
using System.Globalization;
using System.Text;

namespace StripperClips.Framework;

public static class TraceLine
{
    public static string Format(string tag, params (string Key, object? Value)[] fields)
    {
        var line = new StringBuilder(tag);
        foreach (var (key, value) in fields)
            line.Append(' ').Append(key).Append('=').Append(Value(value));
        return line.ToString();
    }

    public static string Value(object? value)
    {
        switch (value)
        {
            case null: return "null";
            case bool flag: return flag ? "true" : "false";
            case float single: return single.ToString("0.####", CultureInfo.InvariantCulture);
            case double number: return number.ToString("0.####", CultureInfo.InvariantCulture);
            case IFormattable formattable: return Escape(formattable.ToString(null, CultureInfo.InvariantCulture));
            default: return Escape(value.ToString() ?? "null");
        }
    }

    private static string Escape(string text) =>
        text.Length == 0 ? "\"\"" : text.Replace(' ', '_').Replace('\n', '_').Replace('\r', '_');
}
