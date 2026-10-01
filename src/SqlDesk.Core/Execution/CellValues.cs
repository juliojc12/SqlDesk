using System.Globalization;
using System.Text;

namespace SqlDesk.Core.Execution;

/// <summary>
/// Converte valores do SQL Server para algo que sobrevive ao JSON sem perder informação: números inteiros e
/// <c>float</c> viram número; <c>decimal</c> e <c>bigint</c> viram texto (o JS só tem double); datas viram ISO.
/// </summary>
public static class CellValues
{
    /// <summary>Texto maior que isso é cortado na grade (a exportação lê o valor completo).</summary>
    public const int MaxTextLength = 50_000;

    private const int MaxBinaryBytes = 64;

    public static string KindOf(Type type)
    {
        var t = Nullable.GetUnderlyingType(type) ?? type;
        if (t == typeof(bool)) return "bool";
        if (t == typeof(byte) || t == typeof(short) || t == typeof(int) || t == typeof(long) ||
            t == typeof(float) || t == typeof(double) || t == typeof(decimal)) return "number";
        if (t == typeof(DateTime) || t == typeof(DateTimeOffset) || t == typeof(DateOnly)) return "date";
        if (t == typeof(byte[])) return "binary";
        return "text";
    }

    public static object? Convert(object? value, string? dataTypeName = null)
    {
        switch (value)
        {
            case null or DBNull: return null;
            case string s: return s.Length > MaxTextLength ? s[..MaxTextLength] + "…" : s;
            case bool or byte or short or int or float or double: return value;
            case long l: return l.ToString(CultureInfo.InvariantCulture);
            case decimal d: return d.ToString(CultureInfo.InvariantCulture);
            case DateTime dt:
                return string.Equals(dataTypeName, "date", StringComparison.OrdinalIgnoreCase)
                    ? dt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                    : dt.ToString("yyyy-MM-dd HH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture);
            case DateTimeOffset dto: return dto.ToString("yyyy-MM-dd HH:mm:ss.FFFFFFF zzz", CultureInfo.InvariantCulture);
            case DateOnly d: return d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            case TimeSpan ts: return ts.ToString(@"hh\:mm\:ss\.FFFFFFF", CultureInfo.InvariantCulture).TrimEnd('.');
            case byte[] bytes: return HexOf(bytes);
            case Guid g: return g.ToString();
            default: return System.Convert.ToString(value, CultureInfo.InvariantCulture);
        }
    }

    private static string HexOf(byte[] bytes)
    {
        var take = Math.Min(bytes.Length, MaxBinaryBytes);
        var sb = new StringBuilder(2 + take * 2 + 1);
        sb.Append("0x");
        for (var i = 0; i < take; i++) sb.Append(bytes[i].ToString("X2", CultureInfo.InvariantCulture));
        if (bytes.Length > take) sb.Append('…');
        return sb.ToString();
    }
}
