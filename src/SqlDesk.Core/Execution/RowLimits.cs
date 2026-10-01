namespace SqlDesk.Core.Execution;

/// <summary>Limite de linhas por result set na grade. Vem da configuração do usuário, mas nunca é aceito sem ajuste de faixa.</summary>
public static class RowLimits
{
    public const int Min = 100;
    public const int Max = 1_000_000;

    /// <summary>Sem limite (<c>null</c>) só quando pedido explicitamente ("Carregar todas"); caso contrário, o pedido ajustado à faixa ou o padrão.</summary>
    public static int? Resolve(int? requested, bool noLimit)
    {
        if (noLimit) return null;
        return requested is { } n ? Math.Clamp(n, Min, Max) : ResultStreamer.DefaultMaxRows;
    }
}
