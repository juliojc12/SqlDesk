using System.Globalization;
using System.Text;
using CsvHelper;
using CsvHelper.Configuration;
using SqlDesk.Core.Execution;
using SqlDesk.Core.Export;

namespace SqlDesk.Core.Tests;

public class CsvQuotingTests
{
    private static string WriteOne(params string?[] values)
    {
        var ms = new MemoryStream();
        using (var w = new CsvRowWriter(ms))
        {
            w.Begin(values.Select((_, i) => new ColumnInfo("C" + i, "text", "nvarchar")).ToList());
            w.Write(values, default);
            w.Complete();
        }
        return new UTF8Encoding(false).GetString(ms.ToArray()).TrimStart('﻿');
    }

    [Theory]
    [InlineData("linha1\nlinha2")]
    [InlineData("linha1\rlinha2")]
    [InlineData("linha1\r\nlinha2")]
    [InlineData("com;ponto e virgula")]
    [InlineData(" espaco no inicio")]
    [InlineData("espaco no fim ")]
    [InlineData("com \"aspas\"")]
    public void Campos_especiais_vao_entre_aspas_e_voltam_iguais_na_leitura(string valor)
    {
        var text = WriteOne(valor);

        Assert.Contains("\"", text.Split("\r\n")[1].Substring(0, 1));
        using var reader = new CsvReader(new StringReader(text), new CsvConfiguration(CultureInfo.InvariantCulture) { Delimiter = ";" });
        reader.Read();
        reader.ReadHeader();
        reader.Read();
        Assert.Equal(valor, reader.GetField(0));
    }

    [Fact]
    public void Nao_cita_o_que_nao_precisa()
    {
        Assert.Equal("C0;C1\r\nsimples;\r\n", WriteOne("simples", null));
    }
}
