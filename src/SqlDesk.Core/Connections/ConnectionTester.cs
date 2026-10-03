using System.Data.Common;
using Microsoft.Data.SqlClient;
using SqlDesk.Core.Providers;

namespace SqlDesk.Core.Connections;

public static class ConnectionTester
{
    public static async Task<TestConnectionResult> TestAsync(ConnectionSettings settings, string? password, CancellationToken ct = default)
    {
        var provider = ProviderRegistry.For(settings);
        try
        {
            await using var conn = provider.CreateConnection(provider.BuildConnectionString(settings, password));
            await conn.OpenAsync(ct);
            return new TestConnectionResult(true, conn.ServerVersion, null);
        }
        catch (DbException ex)
        {
            var f = provider.Translate(ex);
            return new TestConnectionResult(false, null, f.Message, f.CertificateUntrusted);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return new TestConnectionResult(false, null, "Configuração inválida: " + ex.Message);
        }
    }
}

public static partial class SqlErrorTranslator
{
    private const int SslTrustFailure = -2146893019; // SEC_E_UNTRUSTED_ROOT (0x80090325)

    /// <summary>
    /// Um SqlException traz vários erros (um por protocolo tentado); a mensagem de topo costuma ser a do último
    /// (ex.: pipes nomeados) e esconde a causa real. Procura, em todos, o que tem tradução específica.
    /// </summary>
    public static string Translate(SqlException ex)
    {
        foreach (SqlError e in ex.Errors)
            if (Classify(e.Number, e.Message) is { } known) return known;
        return Classify(ex.Number, ex.Message, ex.InnerException as System.ComponentModel.Win32Exception) ?? ex.Message;
    }

    /// <summary>A falha é de confiança no certificado do servidor (o usuário pode optar por confiar).</summary>
    public static bool IsCertificateError(SqlException ex) =>
        ex.Errors.Cast<SqlError>().Any(e => IsCertificate(e.Number, e.Message)) || IsCertificate(ex.Number, ex.Message);

    public static bool IsCertificate(int number, string message) => number == SslTrustFailure || CertificateHint().IsMatch(message);

    public static string? Classify(int number, string message, System.ComponentModel.Win32Exception? inner = null)
    {
        if (IsCertificate(number, message))
            return "O certificado do servidor não é confiável para este computador. Se o servidor é confiável, marque "
                 + "'TrustServerCertificate' (ou desmarque 'Encrypt'). Detalhe: " + message;

        return number switch
        {
            18456 => "Falha de login: usuário ou senha inválidos.",
            18452 or 18470 or 18486 or 18487 => "Falha de login: a conta não pode ser usada (" + message + ")",
            4060 => "O banco de dados informado não existe ou o usuário não tem acesso a ele.",
            0 when inner is { NativeErrorCode: 10060 or 10061 or 11001 } =>
                "Não foi possível alcançar o servidor (recusou a conexão, não respondeu ou o nome não existe). Verifique o nome, a porta e a rede.",
            53 or 11001 => "Não foi possível alcançar o servidor. Verifique o nome, a porta e a rede.",
            -2 => "Tempo esgotado ao conectar. Verifique o servidor ou aumente o timeout de conexão.",
            233 or 10054 or 10060 => "A conexão com o servidor foi recusada ou interrompida.",
            _ => null,
        };
    }

    // Mensagens do SqlClient vêm localizadas (pt-BR: "cadeia de certificação", "Provedor de SSL").
    [System.Text.RegularExpressions.GeneratedRegex(@"certific|SSL Provider|Provedor de SSL|untrusted|n[ãa]o [ée] de confian", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex CertificateHint();
}
