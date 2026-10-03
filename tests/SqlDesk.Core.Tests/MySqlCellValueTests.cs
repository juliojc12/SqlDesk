using SqlDesk.Core.Execution;
using SqlDesk.Core.Export;

namespace SqlDesk.Core.Tests;

public class MySqlCellValueTests
{
    [Fact] public void Ulong_vira_texto() => Assert.Equal("18446744073709551615", CellValues.Convert(ulong.MaxValue));

    [Fact]
    public void Uint_e_ushort_e_sbyte_viram_numero()
    {
        Assert.Equal(5u, CellValues.Convert(5u));
        Assert.Equal((ushort)5, CellValues.Convert((ushort)5));
        Assert.Equal((sbyte)-5, CellValues.Convert((sbyte)-5));
    }

    [Fact] public void Time_longo_do_mysql() => Assert.Equal("838:59:59", CellValues.Convert(new TimeSpan(34, 22, 59, 59)));
    [Fact] public void Time_negativo() => Assert.Equal("-01:30:00", CellValues.Convert(new TimeSpan(-1, -30, 0)));
    [Fact] public void Time_com_fracao() => Assert.Equal("-01:30:00.123", CellValues.Convert(-new TimeSpan(0, 1, 30, 0, 123)));

    [Theory]
    [InlineData(0, 0, 0, 0, "00:00:00")]
    [InlineData(9, 5, 7, 0, "09:05:07")]
    [InlineData(23, 59, 59, 0, "23:59:59")]
    public void Time_normal_continua_igual_ao_formato_antigo(int h, int m, int s, int ms, string esperado) =>
        Assert.Equal(esperado, CellValues.Convert(new TimeSpan(0, h, m, s, ms)));

    [Fact] public void Data_zero_do_mysql() => Assert.Equal("0000-00-00", CellValues.Convert(new MySqlConnector.MySqlDateTime()));

    [Fact]
    public void Data_zero_com_hora_quando_a_coluna_e_datetime() =>
        Assert.Equal("0000-00-00 00:00:00", CellValues.Convert(new MySqlConnector.MySqlDateTime(), "DATETIME"));

    [Fact]
    public void Data_valida_do_mysql_vira_iso() =>
        Assert.Equal("2024-03-05 10:20:30.123", CellValues.Convert(new MySqlConnector.MySqlDateTime(2024, 3, 5, 10, 20, 30, 123000), "DATETIME"));

    [Fact] public void Data_invalida_parcial_nao_estoura() =>
        Assert.Equal("2024-00-05", CellValues.Convert(new MySqlConnector.MySqlDateTime(2024, 0, 5, 0, 0, 0, 0), "DATE"));

    [Fact]
    public void Kind_dos_inteiros_sem_sinal()
    {
        Assert.Equal("number", CellValues.KindOf(typeof(ulong)));
        Assert.Equal("number", CellValues.KindOf(typeof(uint)));
        Assert.Equal("number", CellValues.KindOf(typeof(ushort)));
        Assert.Equal("number", CellValues.KindOf(typeof(sbyte)));
    }

    [Fact] public void Kind_do_mysql_datetime_e_data() => Assert.Equal("date", CellValues.KindOf(typeof(MySqlConnector.MySqlDateTime)));

    [Fact]
    public void Exportacao_formata_time_longo_e_negativo()
    {
        Assert.Equal("838:59:59", ExportValues.ToText(new TimeSpan(34, 22, 59, 59)));
        Assert.Equal("-01:30:00", ExportValues.ToText(new TimeSpan(-1, -30, 0)));
        Assert.Equal("0000-00-00", ExportValues.ToText(new MySqlConnector.MySqlDateTime(), "DATE"));
        Assert.Equal("18446744073709551615", ExportValues.ToText(ulong.MaxValue));
    }
}

public class MySqlExportCellTests
{
    [Fact]
    public void ToCell_inteiros_sem_sinal_e_sbyte_continuam_numeros()
    {
        Assert.Equal((sbyte)-5, ExportValues.ToCell((sbyte)-5));
        Assert.Equal((ushort)7, ExportValues.ToCell((ushort)7));
        Assert.Equal(4000000000u, ExportValues.ToCell(4000000000u));
    }

    [Fact]
    public void ToCell_ulong_vira_decimal_exato()
    {
        var cell = ExportValues.ToCell(ulong.MaxValue);
        Assert.IsType<decimal>(cell);
        Assert.Equal(18446744073709551615m, cell);
    }

    [Fact]
    public void ToCell_data_do_mysql_valida_vira_data_e_zero_vira_texto()
    {
        Assert.Equal(new DateTime(2024, 3, 5, 10, 20, 30), ExportValues.ToCell(new MySqlConnector.MySqlDateTime(2024, 3, 5, 10, 20, 30, 0), "DATETIME"));
        Assert.Equal("0000-00-00", ExportValues.ToCell(new MySqlConnector.MySqlDateTime(), "DATE"));
    }

    [Fact]
    public void FormatTime_nao_estoura_nos_extremos()
    {
        Assert.StartsWith("-", CellValues.FormatTime(TimeSpan.MinValue));
        Assert.DoesNotContain("-", CellValues.FormatTime(TimeSpan.MaxValue));
    }

    [Theory]
    [InlineData(-5_000_000L, "-00:00:00.5")]
    [InlineData(864_000_000_000L, "24:00:00")]
    [InlineData(863_991_234_567L, "23:59:59.1234567")]
    [InlineData(5_000_000L, "00:00:00.5")]
    public void FormatTime_casos_de_borda(long ticks, string esperado) => Assert.Equal(esperado, CellValues.FormatTime(new TimeSpan(ticks)));
}
