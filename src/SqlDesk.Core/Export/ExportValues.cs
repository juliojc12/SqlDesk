using System.Globalization;
using System.Text;
using SqlDesk.Core.Execution;

namespace SqlDesk.Core.Export;

/// <summary>Conversão de valores do SQL Server para texto de CSV e para células de XLSX.</summary>
public static class ExportValues
{
    private static bool IsDateOnlyType(string typeName) => string.Equals(typeName, "date", StringComparison.OrdinalIgnoreCase);

    /// <summary>Texto de CSV: NULL vira vazio, datas em ISO 8601, números com ponto decimal, binário em hexadecimal completo.</summary>
    public static string ToText(object? v, string typeName = "")
    {
        switch (v)
        {
            case null or DBNull: return "";
            case string s: return s;
            case bool b: return b ? "1" : "0";
            case DateTime dt:
                return IsDateOnlyType(typeName)
                    ? dt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                    : dt.ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture);
            case DateTimeOffset dto: return dto.ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz", CultureInfo.InvariantCulture);
            case DateOnly d: return d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            case TimeSpan ts: return ts.ToString(@"hh\:mm\:ss\.FFFFFFF", CultureInfo.InvariantCulture).TrimEnd('.');
            case TimeOnly t: return t.ToString("HH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture).TrimEnd('.');
            case byte[] bytes: return Hex(bytes);
            case Guid g: return g.ToString();
            case double dbl: return dbl.ToString("R", CultureInfo.InvariantCulture);
            case float f: return f.ToString("R", CultureInfo.InvariantCulture);
            case IFormattable fmt: return fmt.ToString(null, CultureInfo.InvariantCulture);
            default: return Convert.ToString(v, CultureInfo.InvariantCulture) ?? "";
        }
    }

    /// <summary>Célula de XLSX: números continuam números, datas continuam datas, o resto vira texto.</summary>
    public static object? ToCell(object? v, string typeName = "")
    {
        switch (v)
        {
            case null or DBNull: return null;
            case string or bool or byte or short or int or long or float or double or decimal or DateTime: return v;
            case DateOnly d: return d.ToDateTime(TimeOnly.MinValue);
            default: return ToText(v, typeName); // DateTimeOffset, TimeSpan, Guid, binário...
        }
    }

    private static string Hex(byte[] bytes)
    {
        var sb = new StringBuilder(2 + bytes.Length * 2);
        sb.Append("0x");
        foreach (var b in bytes) sb.Append(b.ToString("X2", CultureInfo.InvariantCulture));
        return sb.ToString();
    }

    /// <summary>
    /// Volta ao tipo original um valor que passou pelo JSON e pela grade (decimal e bigint chegam como texto, datas em ISO),
    /// para que o XLSX preserve números e datas. Se não for possível converter, mantém o valor como veio.
    /// </summary>
    public static object? Coerce(object? v, ColumnInfo column)
    {
        if (v is not string s) return v;
        var type = column.TypeName.ToLowerInvariant();
        switch (column.Kind)
        {
            case "number":
                if (type is "bigint" && long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l)) return l;
                if (decimal.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) return d;
                return s;
            case "date":
                if (type is "datetimeoffset") return DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var o) ? o : s;
                return DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt) ? dt : s;
            default:
                return s;
        }
    }
}
