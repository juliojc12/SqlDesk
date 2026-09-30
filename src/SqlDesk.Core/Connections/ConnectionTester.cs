using Microsoft.Data.SqlClient;

namespace SqlDesk.Core.Connections;

public static class ConnectionTester
{
    public static async Task<TestConnectionResult> TestAsync(ConnectionSettings settings, string? password, CancellationToken ct = default)
    {
        try
        {
            await using var conn = new SqlConnection(ConnectionStringService.Build(settings, password));
            await conn.OpenAsync(ct);
            return new TestConnectionResult(true, conn.ServerVersion, null);
        }
        catch (SqlException ex)
        {
            return new TestConnectionResult(false, null, SqlErrorTranslator.Translate(ex));
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return new TestConnectionResult(false, null, "Configuração inválida: " + ex.Message);
        }
    }
}

public static class SqlErrorTranslator
{
    public static string Translate(SqlException ex) => ex.Number switch
    {
        18456 => "Falha de login: usuário ou senha inválidos.",
        18452 or 18470 or 18486 or 18487 => "Falha de login: a conta não pode ser usada (" + ex.Message + ")",
        4060 => "O banco de dados informado não existe ou o usuário não tem acesso a ele.",
        0 when ex.InnerException is System.ComponentModel.Win32Exception { NativeErrorCode: 10060 or 10061 or 11001 } =>
            "Não foi possível alcançar o servidor (recusou a conexão, não respondeu ou o nome não existe). Verifique o nome, a porta e a rede.",
        53 or 40 or 2 or 11001 => "Não foi possível alcançar o servidor. Verifique o nome, a porta e a rede.",
        -2 => "Tempo esgotado ao conectar. Verifique o servidor ou aumente o timeout de conexão.",
        233 or 10054 or 10060 => "A conexão com o servidor foi recusada ou interrompida.",
        _ when ex.Message.Contains("certificate", StringComparison.OrdinalIgnoreCase) =>
            "Falha na validação do certificado do servidor. Se confia no servidor, marque 'Confiar no certificado'. (" + ex.Message + ")",
        _ => ex.Message,
    };
}
