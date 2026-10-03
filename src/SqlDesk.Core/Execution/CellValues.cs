using System.Globalization;
using System.Text;
using MySqlConnector;

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
        if (t == typeof(byte) || t == typeof(sbyte) || t == typeof(short) || t == typeof(ushort) || t == typeof(uint) || t == typeof(ulong) || t == typeof(int) || t == typeof(long) ||
            t == typeof(float) || t == typeof(double) || t == typeof(decimal)) return "number";
        if (t == typeof(DateTime) || t == typeof(DateTimeOffset) || t == typeof(DateOnly) || t == typeof(MySqlDateTime)) return "date";
        if (t == typeof(byte[])) return "binary";
        return "text";
    }

    public static object? Convert(object? value, string? dataTypeName = null)
    {
        switch (value)
        {
            case null or DBNull: return null;
            case string s: return s.Length > MaxTextLength ? s[..MaxTextLength] + "…" : s;
            case bool or byte or sbyte or short or ushort or int or uint or float or double: return value;
            case long l: return l.ToString(CultureInfo.InvariantCulture);
            case ulong ul: return ul.ToString(CultureInfo.InvariantCulture);
            case decimal d: return d.ToString(CultureInfo.InvariantCulture);
            case DateTime dt:
                return string.Equals(dataTypeName, "date", StringComparison.OrdinalIgnoreCase)
                    ? dt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                    : dt.ToString("yyyy-MM-dd HH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture);
            case DateTimeOffset dto: return dto.ToString("yyyy-MM-dd HH:mm:ss.FFFFFFF zzz", CultureInfo.InvariantCulture);
            case DateOnly d: return d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            case MySqlDateTime mdt: return FormatMySqlDateTime(mdt, dataTypeName, ' ');
            case TimeSpan ts: return FormatTime(ts);
            case byte[] bytes: return HexOf(bytes);
            case Guid g: return g.ToString();
            default: return System.Convert.ToString(value, CultureInfo.InvariantCulture);
        }
    }

    /// <summary>
    /// Formata um intervalo de tempo como <c>[-]HH:mm:ss[.fff]</c>. O <c>TIME</c> do MySQL vai de -838:59:59 a 838:59:59,
    /// então as horas não se limitam a 24 (abaixo disso o resultado é o mesmo do formato <c>hh:mm:ss</c> do SQL Server).
    /// </summary>
    public static string FormatTime(TimeSpan ts)
    {
        var abs = ts == TimeSpan.MinValue ? TimeSpan.MaxValue : ts.Duration(); // Duration() estoura em MinValue
        var sb = new StringBuilder();
        if (ts < TimeSpan.Zero) sb.Append('-');
        sb.Append(((long)abs.TotalHours).ToString("00", CultureInfo.InvariantCulture));
        sb.Append(abs.ToString(@"\:mm\:ss\.FFFFFFF", CultureInfo.InvariantCulture).TrimEnd('.'));
        return sb.ToString();
    }

    /// <summary>
    /// Texto de um <c>MySqlDateTime</c>. Datas válidas seguem o formato ISO dos <c>DateTime</c>; datas zero ou
    /// parciais (<c>0000-00-00</c>, <c>2024-00-05</c>), que o .NET não representa, saem campo a campo sem estourar.
    /// </summary>
    public static string FormatMySqlDateTime(MySqlDateTime v, string? dataTypeName, char separator)
    {
        // Sem o nome do tipo (só quando o valor chega solto), vale o que o próprio valor mostra: sem hora, é data.
        var dateOnly = dataTypeName is null
            ? v.Hour == 0 && v.Minute == 0 && v.Second == 0 && v.Microsecond == 0
            : string.Equals(dataTypeName, "date", StringComparison.OrdinalIgnoreCase);
        if (v.IsValidDateTime)
        {
            var dt = v.GetDateTime();
            return dateOnly
                ? dt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                : dt.ToString("yyyy-MM-dd'" + separator + "'HH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture);
        }
        var date = $"{v.Year:D4}-{v.Month:D2}-{v.Day:D2}";
        if (dateOnly) return date;
        var time = $"{v.Hour:D2}:{v.Minute:D2}:{v.Second:D2}";
        if (v.Microsecond != 0) time += "." + v.Microsecond.ToString("D6", CultureInfo.InvariantCulture).TrimEnd('0');
        return date + separator + time;
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
