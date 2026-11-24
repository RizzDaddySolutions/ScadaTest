using System.Globalization;

namespace ScadaTest.Core;

/// <summary>Compares SCADA values loosely: numbers across types, booleans from 0/1 or "true", strings case-insensitively.</summary>
public static class ValueComparer
{
    public static bool AreEqual(object? actual, object? expected, double tolerance = 0)
    {
        if (actual is null || expected is null)
            return actual is null && expected is null;

        if (TryBool(expected, out var eb) && expected is bool or string && TryBool(actual, out var ab))
            return ab == eb;

        if (TryDouble(actual, out var ad) && TryDouble(expected, out var ed))
            return Math.Abs(ad - ed) <= tolerance;

        return string.Equals(
            Convert.ToString(actual, CultureInfo.InvariantCulture),
            Convert.ToString(expected, CultureInfo.InvariantCulture),
            StringComparison.OrdinalIgnoreCase);
    }

    public static bool TryDouble(object? value, out double result)
    {
        switch (value)
        {
            case bool b: result = b ? 1 : 0; return true;
            case string s: return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out result);
            case IConvertible c when value is not char and not DateTime:
                try { result = c.ToDouble(CultureInfo.InvariantCulture); return true; }
                catch (Exception e) when (e is FormatException or InvalidCastException or OverflowException) { break; }
        }
        result = 0;
        return false;
    }

    public static bool TryBool(object? value, out bool result)
    {
        switch (value)
        {
            case bool b: result = b; return true;
            case string s when bool.TryParse(s, out result): return true;
            case string s when s is "1" or "0": result = s == "1"; return true;
            case string s when s.Equals("on", StringComparison.OrdinalIgnoreCase): result = true; return true;
            case string s when s.Equals("off", StringComparison.OrdinalIgnoreCase): result = false; return true;
            case int or long or short or byte or uint or ulong or ushort or sbyte:
                var d = Convert.ToInt64(value, CultureInfo.InvariantCulture);
                if (d is 0 or 1) { result = d == 1; return true; }
                break;
        }
        result = false;
        return false;
    }

    public static string Format(object? value) => value switch
    {
        null => "null",
        string s => $"\"{s}\"",
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "null",
    };
}
