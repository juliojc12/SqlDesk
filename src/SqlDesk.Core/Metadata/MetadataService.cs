using System.Collections.Concurrent;
using System.Data.Common;
using SqlDesk.Core.Providers;

namespace SqlDesk.Core.Metadata;

/// <summary>Tipo de objeto: table, view, procedure ou function.</summary>
public sealed record MetaObject(string Schema, string Name, string Type);

public sealed record MetaColumn(string Name, string Type, bool Nullable);

/// <summary>Cache de uma conexão. <see cref="Columns"/> é indexado por <c>schema.objeto</c> e só vale com <see cref="ColumnsLoaded"/>.</summary>
public sealed record MetadataSnapshot(
    IReadOnlyList<string> Schemas,
    IReadOnlyList<MetaObject> Objects,
    IReadOnlyDictionary<string, IReadOnlyList<MetaColumn>> Columns,
    bool ColumnsLoaded,
    DateTimeOffset LoadedAt,
    bool HasSchemaLevel = true);

public static class MetaPhase
{
    public const string Objects = "objects";
    public const string Columns = "columns";
    public const string Error = "error";
}

public static class MetadataQueries
{
    public const string Objects =
        "SELECT s.name, o.name, o.type FROM sys.objects o JOIN sys.schemas s ON s.schema_id = o.schema_id " +
        "WHERE o.is_ms_shipped = 0 AND o.type IN ('U','V','P','FN','IF','TF') ORDER BY s.name, o.name";

    public const string Columns =
        "SELECT s.name, o.name, c.name, t.name, c.max_length, c.precision, c.scale, c.is_nullable " +
        "FROM sys.columns c JOIN sys.objects o ON o.object_id = c.object_id " +
        "JOIN sys.schemas s ON s.schema_id = o.schema_id JOIN sys.types t ON t.user_type_id = c.user_type_id " +
        "WHERE o.is_ms_shipped = 0 AND o.type IN ('U','V','IF','TF') ORDER BY s.name, o.name, c.column_id";
}

/// <summary>Leitura dos result sets de metadados, separada da conexão para ser testável.</summary>
public static class MetadataReader
{
    public static string ObjectKind(string sysType) => sysType.Trim() switch
    {
        "U" => "table",
        "V" => "view",
        "P" => "procedure",
        _ => "function", // FN, IF, TF
    };

    public static string Key(string schema, string name) => $"{schema}.{name}";

    /// <summary>Lê a consulta de objetos do SQL Server.</summary>
    public static Task<List<MetaObject>> ReadObjectsAsync(DbDataReader r, CancellationToken ct) =>
        ReadObjectsAsync(r, ProviderRegistry.Get(ProviderIds.SqlServer).Metadata, ct);

    /// <summary>Lê a consulta de objetos (schema, nome, tipo bruto) de qualquer provedor.</summary>
    public static async Task<List<MetaObject>> ReadObjectsAsync(DbDataReader r, IMetadataSql meta, CancellationToken ct)
    {
        var list = new List<MetaObject>();
        while (await r.ReadAsync(ct))
            list.Add(new MetaObject(r.GetString(0), r.GetString(1), meta.ObjectKind(r.GetString(2))));
        return list;
    }

    /// <summary>Lê a consulta de colunas do SQL Server.</summary>
    public static Task<Dictionary<string, IReadOnlyList<MetaColumn>>> ReadColumnsAsync(DbDataReader r, CancellationToken ct) =>
        ReadColumnsAsync(r, ProviderRegistry.Get(ProviderIds.SqlServer).Metadata, ct);

    /// <summary>Lê a consulta de colunas (schema, objeto, coluna, ..., nullable na coluna 7) de qualquer provedor.</summary>
    public static async Task<Dictionary<string, IReadOnlyList<MetaColumn>>> ReadColumnsAsync(DbDataReader r, IMetadataSql meta, CancellationToken ct)
    {
        var map = new Dictionary<string, List<MetaColumn>>(StringComparer.OrdinalIgnoreCase);
        while (await r.ReadAsync(ct))
        {
            var key = Key(r.GetString(0), r.GetString(1));
            if (!map.TryGetValue(key, out var cols)) map[key] = cols = [];
            cols.Add(new MetaColumn(r.GetString(2), meta.FormatType(r), Convert.ToBoolean(r.GetValue(7))));
        }
        return map.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<MetaColumn>)kv.Value, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Tipo como se escreve no T-SQL: <c>varchar(50)</c>, <c>nvarchar(max)</c>, <c>decimal(10,2)</c>.</summary>
    public static string FormatType(string name, int maxLength, int precision, int scale)
    {
        switch (name.ToLowerInvariant())
        {
            case "varchar" or "char" or "varbinary" or "binary":
                return maxLength == -1 ? $"{name}(max)" : $"{name}({maxLength})";
            case "nvarchar" or "nchar": // max_length é em bytes
                return maxLength == -1 ? $"{name}(max)" : $"{name}({maxLength / 2})";
            case "decimal" or "numeric":
                return $"{name}({precision},{scale})";
            case "datetime2" or "datetimeoffset" or "time":
                return $"{name}({scale})";
            default:
                return name;
        }
    }

    /// <summary>Schemas que contêm objetos, em ordem alfabética, com <c>dbo</c> primeiro.</summary>
    public static List<string> SchemasOf(IEnumerable<MetaObject> objects) =>
        objects.Select(o => o.Schema).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(s => string.Equals(s, "dbo", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(s => s, StringComparer.OrdinalIgnoreCase).ToList();
}

/// <summary>
/// Cache de metadados por conexão, em memória. Carrega primeiro schemas e objetos, e em seguida as colunas, numa
/// conexão própria (não a de uma aba), avisando o frontend a cada fase.
/// </summary>
public sealed class MetadataService
{
    private readonly Func<Guid, Action<string, string?>, Task> _loader;

    public MetadataService(Func<Guid, CancellationToken, Task<(DbConnection Connection, IDatabaseProvider Provider)>> openConnection)
    {
        _loader = (id, onPhase) => LoadAsync(openConnection, id, onPhase);
    }

    /// <summary>Com um carregador próprio (usado nos testes, que não têm servidor). Ele só avisa a fase "objects"; a fase final é do serviço.</summary>
    public MetadataService(Func<Guid, Action<string, string?>, Task> loader) => _loader = loader;

    private readonly ConcurrentDictionary<Guid, MetadataSnapshot> _cache = new();
    private readonly ConcurrentDictionary<Guid, byte> _loading = new();

    public MetadataSnapshot? Get(Guid connectionId) => _cache.TryGetValue(connectionId, out var s) ? s : null;

    public bool IsLoading(Guid connectionId) => _loading.ContainsKey(connectionId);

    public void Invalidate(Guid connectionId) => _cache.TryRemove(connectionId, out _);

    /// <summary>
    /// Inicia o carregamento em segundo plano. Devolve false se já há um em andamento ou, sem <paramref name="force"/>,
    /// se o cache já está completo. <paramref name="onPhase"/> recebe "objects", "columns" ou "error" (com a mensagem).
    /// </summary>
    public bool StartLoad(Guid connectionId, bool force, Action<string, string?> onPhase)
    {
        if (!force && Get(connectionId) is { ColumnsLoaded: true }) return false;
        if (!_loading.TryAdd(connectionId, 0)) return false;

        _ = Task.Run(async () =>
        {
            string? error = null;
            try { await _loader(connectionId, onPhase); }
            catch (Exception ex) { error = ex.Message; }

            // O carregamento precisa constar como terminado ANTES do aviso final: o frontend reage ao aviso consultando o estado,
            // e se ainda visse "carregando" ficaria esperando para sempre um aviso que não vem mais.
            _loading.TryRemove(connectionId, out _);
            if (error is null) onPhase(MetaPhase.Columns, null);
            else onPhase(MetaPhase.Error, error);
        });
        return true;
    }

    private async Task LoadAsync(
        Func<Guid, CancellationToken, Task<(DbConnection Connection, IDatabaseProvider Provider)>> openConnection,
        Guid connectionId, Action<string, string?> onPhase)
    {
        var (connection, provider) = await openConnection(connectionId, CancellationToken.None);
        await using var conn = connection;
        var meta = provider.Metadata;

        List<MetaObject> objects;
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = meta.ObjectsSql;
            cmd.CommandTimeout = 60;
            await using var reader = await cmd.ExecuteReaderAsync();
            objects = await MetadataReader.ReadObjectsAsync(reader, meta, default);
        }

        var empty = new Dictionary<string, IReadOnlyList<MetaColumn>>();
        _cache[connectionId] = new MetadataSnapshot(MetadataReader.SchemasOf(objects), objects, empty, false, DateTimeOffset.UtcNow, meta.HasSchemaLevel);
        onPhase(MetaPhase.Objects, null);

        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = meta.ColumnsSql;
            cmd.CommandTimeout = 120;
            await using var reader = await cmd.ExecuteReaderAsync();
            var columns = await MetadataReader.ReadColumnsAsync(reader, meta, default);
            _cache[connectionId] = new MetadataSnapshot(MetadataReader.SchemasOf(objects), objects, columns, true, DateTimeOffset.UtcNow, meta.HasSchemaLevel);
        }
    }
}
