using SqlDesk.Core.Connections;

namespace SqlDesk.Core.Providers;

public static class ProviderRegistry
{
    private static readonly Dictionary<string, IDatabaseProvider> Providers = new IDatabaseProvider[] { new SqlServerProvider(), new MySqlProvider() }
        .ToDictionary(p => p.Id);

    public static IReadOnlyCollection<IDatabaseProvider> All => Providers.Values;

    public static IDatabaseProvider Get(string id) =>
        Providers.TryGetValue(id, out var p) ? p : throw new ConnectionValidationException($"Tipo de servidor desconhecido: {id}.");

    public static IDatabaseProvider For(ConnectionSettings settings) => Get(settings.Provider);
}
