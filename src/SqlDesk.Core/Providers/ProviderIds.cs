namespace SqlDesk.Core.Providers;

/// <summary>Identificadores estáveis dos bancos suportados (gravados em connections.json e trafegados na ponte).</summary>
public static class ProviderIds
{
    public const string SqlServer = "sqlserver";
    public const string MySql = "mysql";

    public static bool IsKnown(string? id) => id is SqlServer or MySql;
}
