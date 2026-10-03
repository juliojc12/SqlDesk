using System.Data;
using SqlDesk.Core.Metadata;
using SqlDesk.Core.Providers;

namespace SqlDesk.Core.Tests;

public class MetadataTests
{
    [Theory]
    [InlineData("varchar", 50, 0, 0, "varchar(50)")]
    [InlineData("varchar", -1, 0, 0, "varchar(max)")]
    [InlineData("nvarchar", 100, 0, 0, "nvarchar(50)")] // max_length em bytes
    [InlineData("nvarchar", -1, 0, 0, "nvarchar(max)")]
    [InlineData("char", 3, 0, 0, "char(3)")]
    [InlineData("varbinary", -1, 0, 0, "varbinary(max)")]
    [InlineData("decimal", 9, 10, 2, "decimal(10,2)")]
    [InlineData("numeric", 9, 18, 0, "numeric(18,0)")]
    [InlineData("datetime2", 8, 27, 7, "datetime2(7)")]
    [InlineData("int", 4, 10, 0, "int")]
    [InlineData("bit", 1, 1, 0, "bit")]
    [InlineData("uniqueidentifier", 16, 0, 0, "uniqueidentifier")]
    public void Formata_o_tipo_como_no_TSQL(string name, int max, int precision, int scale, string esperado) =>
        Assert.Equal(esperado, MetadataReader.FormatType(name, max, precision, scale));

    [Theory]
    [InlineData("U ", "table")]
    [InlineData("V", "view")]
    [InlineData("P", "procedure")]
    [InlineData("FN", "function")]
    [InlineData("IF", "function")]
    [InlineData("TF", "function")]
    public void Classifica_o_tipo_do_objeto(string sys, string kind) => Assert.Equal(kind, MetadataReader.ObjectKind(sys));

    [Theory]
    [InlineData("decimal(10,2)")]
    [InlineData("varchar(50)")]
    [InlineData("tinyint(1)")]
    [InlineData("int")]
    public void MySql_usa_COLUMN_TYPE_como_veio(string columnType)
    {
        var t = new DataTable();
        foreach (var c in new[] { "s", "o", "c", "type" }) t.Columns.Add(c, typeof(string));
        t.Rows.Add("db", "tab", "col", columnType);
        using var reader = t.CreateDataReader();
        reader.Read();
        Assert.Equal(columnType, ProviderRegistry.Get(ProviderIds.MySql).Metadata.FormatType(reader));
    }

    [Theory]
    [InlineData("U", "table")]
    [InlineData("V", "view")]
    [InlineData("P", "procedure")]
    [InlineData("FN", "function")]
    public void MySql_classifica_o_tipo_do_objeto(string raw, string kind) =>
        Assert.Equal(kind, ProviderRegistry.Get(ProviderIds.MySql).Metadata.ObjectKind(raw));

    [Fact]
    public void MySql_nao_tem_nivel_de_schema_e_sql_Server_tem()
    {
        Assert.False(ProviderRegistry.Get(ProviderIds.MySql).Metadata.HasSchemaLevel);
        Assert.True(ProviderRegistry.Get(ProviderIds.SqlServer).Metadata.HasSchemaLevel);
    }

    [Fact]
    public async Task Le_objetos_e_deriva_os_schemas_com_dbo_primeiro()
    {
        var t = new DataTable();
        t.Columns.Add("s", typeof(string)); t.Columns.Add("n", typeof(string)); t.Columns.Add("t", typeof(string));
        t.Rows.Add("vendas", "Pedidos", "U "); t.Rows.Add("dbo", "Clientes", "U "); t.Rows.Add("dbo", "vwAtivos", "V ");
        t.Rows.Add("rh", "pr_Folha", "P ");

        var objects = await MetadataReader.ReadObjectsAsync(t.CreateDataReader(), default);

        Assert.Equal(4, objects.Count);
        Assert.Equal(new MetaObject("dbo", "vwAtivos", "view"), objects[2]);
        Assert.Equal(["dbo", "rh", "vendas"], MetadataReader.SchemasOf(objects));
    }

    [Fact]
    public async Task Agrupa_as_colunas_por_objeto_na_ordem_recebida_ignorando_caixa_na_chave()
    {
        var t = new DataTable();
        t.Columns.Add("s", typeof(string)); t.Columns.Add("o", typeof(string)); t.Columns.Add("c", typeof(string)); t.Columns.Add("t", typeof(string));
        t.Columns.Add("max", typeof(short)); t.Columns.Add("p", typeof(byte)); t.Columns.Add("sc", typeof(byte)); t.Columns.Add("nul", typeof(bool));
        t.Rows.Add("dbo", "Clientes", "Id", "int", (short)4, (byte)10, (byte)0, false);
        t.Rows.Add("dbo", "Clientes", "Nome", "nvarchar", (short)100, (byte)0, (byte)0, true);
        t.Rows.Add("dbo", "Pedidos", "Valor", "decimal", (short)9, (byte)12, (byte)2, false);

        var map = await MetadataReader.ReadColumnsAsync(t.CreateDataReader(), default);

        Assert.Equal(["Id", "Nome"], map["dbo.clientes"].Select(c => c.Name));
        Assert.Equal("nvarchar(50)", map["dbo.Clientes"][1].Type);
        Assert.True(map["dbo.Clientes"][1].Nullable);
        Assert.Equal("decimal(12,2)", map["dbo.Pedidos"][0].Type);
    }

    [Fact]
    public void Consultas_de_metadados_sao_somente_leitura_e_nao_incluem_objetos_do_sistema()
    {
        foreach (var q in new[] { MetadataQueries.Objects, MetadataQueries.Columns })
        {
            Assert.StartsWith("SELECT", q);
            Assert.Contains("is_ms_shipped = 0", q);
            Assert.DoesNotContain("UPDATE", q, StringComparison.OrdinalIgnoreCase);
        }
    }
}
