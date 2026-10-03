using System.Data.Common;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using MySqlConnector;
using SqlDesk.Core.Connections;
using SqlDesk.Core.Metadata;
using SqlDesk.SqlAnalysis;

namespace SqlDesk.Core.Providers;

/// <summary>Provedor do MySQL/MariaDB (driver MySqlConnector).</summary>
public sealed class MySqlProvider : IDatabaseProvider
{
    private const int MySqlDefaultPort = 3306;

    // Chaves tratadas por campos próprios (nome normalizado: sem espaços/sublinhados, minúsculas).
    private static readonly HashSet<string> Handled = new(StringComparer.Ordinal)
    {
        "server", "port", "database", "userid", "password", "sslmode", "connectiontimeout", "defaultcommandtimeout",
    };

    /// <summary>
    /// Chaves fora do escopo (nome canônico normalizado): autenticação por certificado, arquivos locais (chave, CA,
    /// chave RSA), socket/pipe/memória compartilhada, Kerberos e LOAD DATA LOCAL. Só usuário e senha são suportados, e
    /// o TLS vem das caixas do formulário (SslMode). Aliases (<c>Protocol</c>, <c>Pipe</c>, <c>Ssl-Cert</c>,
    /// <c>CA Certificate File</c>...) passam pelo nome canônico do driver antes da comparação.
    /// </summary>
    private static readonly HashSet<string> OutOfScopeKeys = new(StringComparer.Ordinal)
    {
        "allowloadlocalinfile", "certificatefile", "certificatepassword", "certificatestorelocation", "certificatethumbprint",
        "sslcert", "sslkey", "sslca", "cacertificatefile", "connectionprotocol", "pipename", "sharedmemoryname",
        "serverrsapublickeyfile", "serverspn", "integratedsecurity",
    };

    private static readonly Regex CertificateText = new(@"\b(SSL|TLS)\b|certificate", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex LineText = new(@"at line (\d+)", RegexOptions.CultureInvariant);

    public string Id => ProviderIds.MySql;
    public string DisplayName => "MySQL / MariaDB";
    public int DefaultPort => MySqlDefaultPort;
    public ISqlAnalyzer Analyzer => MySqlAnalyzer.Instance;
    public bool DdlCommitsImplicitly => true;

    public ITransactionSql Transactions { get; } = new MySqlTransactionSql();

    public IMetadataSql Metadata { get; } = new MySqlMetadataSql();

    public string BuildConnectionString(ConnectionSettings s, string? password)
    {
        var (host, port) = ParseServer(s.Server);
        var b = new MySqlConnectionStringBuilder
        {
            Server = host,
            Port = port,
            Database = s.Database,
            UserID = s.User,
            ConnectionTimeout = (uint)Math.Max(0, s.ConnectTimeout),
            DefaultCommandTimeout = (uint)Math.Max(0, s.CommandTimeout),
            SslMode = !s.Encrypt ? MySqlSslMode.None : s.TrustServerCertificate ? MySqlSslMode.Required : MySqlSslMode.VerifyCA,
            AllowUserVariables = true,
            CharacterSet = "utf8mb4",
            // Sem isto, ler uma data 0000-00-00 estoura no driver e derruba a grade inteira; com isto ela chega como
            // MySqlDateTime e o CellValues a mostra como 0000-00-00. O usuário pode desligar em Avançado.
            AllowZeroDateTime = true,
        };
        if (!string.IsNullOrEmpty(password)) b.Password = password;
        ValidateAdvanced(s.Advanced);
        foreach (var (key, value) in s.Advanced)
        {
            try { b[key] = value; }
            catch (ArgumentException) { throw new ConnectionValidationException($"Palavra-chave não suportada: '{key}'."); }
        }
        return b.ConnectionString;
    }

    /// <summary>
    /// Confere as chaves avançadas de uma conexão (arquivo salvo, editado à mão ou vindo da interface): recusa as fora do
    /// escopo e as que têm campo próprio (senha, servidor, usuário, SslMode...), que em Avançado sobrescreveriam o
    /// formulário (e a senha iria em texto puro para o arquivo).
    /// </summary>
    public static void ValidateAdvanced(IReadOnlyDictionary<string, string> advanced)
    {
        foreach (var (key, value) in advanced)
        {
            var canonical = CanonicalKey(key, value);
            var norm = Normalize(canonical);
            CheckInScope(key, norm, value);
            if (Handled.Contains(norm))
                throw new ConnectionValidationException($"'{key}' não vai em Avançado: use o campo próprio do formulário.");
        }
    }

    private static string Normalize(string key) => key.Replace(" ", "").Replace("_", "").Replace("-", "").ToLowerInvariant();

    private static void CheckInScope(string key, string norm, string value)
    {
        if (!OutOfScopeKeys.Contains(norm)) return;
        // TCP (o padrão) pode vir escrito; Unix, Pipe e Memory não.
        if (norm == "connectionprotocol" && IsTcp(key, value)) return;
        throw new ConnectionValidationException(
            $"{key} está fora do escopo: só autenticação por usuário e senha (e SslMode pelas caixas) é suportada.");
    }

    private static bool IsTcp(string key, string value)
    {
        try { return new MySqlConnectionStringBuilder { [key] = value }.ConnectionProtocol == MySqlConnectionProtocol.Sockets; }
        catch (ArgumentException) { return false; }
        catch (FormatException) { return false; }
    }

    public (ConnectionSettings Settings, string? Password) ParseConnectionString(string connectionString)
    {
        var raw = new DbConnectionStringBuilder();
        MySqlConnectionStringBuilder b;
        try
        {
            raw.ConnectionString = connectionString;
            b = new MySqlConnectionStringBuilder(connectionString);
        }
        catch (ArgumentException ex) { throw new ConnectionValidationException("Connection string inválida: " + ex.Message); }
        catch (FormatException ex) { throw new ConnectionValidationException("Connection string inválida: " + ex.Message); }

        var advanced = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? password = null;
        foreach (string key in raw.Keys)
        {
            var value = Convert.ToString(raw[key]) ?? "";
            var canonical = CanonicalKey(key, value);
            var norm = Normalize(canonical);
            CheckInScope(key, norm, value);
            if (norm == "password") password = b.Password;
            else if (!Handled.Contains(norm)) advanced[canonical.Replace(" ", "")] = value;
        }

        var host = b.Server.Contains(':') ? $"[{b.Server}]" : b.Server;
        var server = b.Port != MySqlDefaultPort ? $"{host}:{b.Port}" : b.Server;
        var (encrypt, trust) = b.SslMode switch
        {
            MySqlSslMode.None or MySqlSslMode.Disabled => (false, false),
            MySqlSslMode.VerifyCA or MySqlSslMode.VerifyFull => (true, false),
            _ => (true, true), // Preferred, Required
        };
        var settings = new ConnectionSettings(server, b.Database, b.UserID, (int)b.ConnectionTimeout, (int)b.DefaultCommandTimeout, encrypt, trust, advanced)
        {
            Provider = ProviderIds.MySql,
        };
        return (settings, password);
    }

    /// <summary>Nome canônico da palavra-chave (como o builder grava); rejeita chaves desconhecidas.</summary>
    private static string CanonicalKey(string key, string value)
    {
        try
        {
            var text = new MySqlConnectionStringBuilder { [key] = value }.ConnectionString;
            return text[..text.IndexOf('=')];
        }
        catch (ArgumentException)
        {
            throw new ConnectionValidationException($"Palavra-chave não suportada: '{key}'.");
        }
        catch (FormatException)
        {
            throw new ConnectionValidationException($"Valor inválido para '{key}': '{value}'.");
        }
    }

    /// <summary>
    /// Separa o alvo como o analisador o devolve (<c>t</c>, <c>db.t</c>, <c>`db`.`t`</c>, <c>`we``ird`</c>) em banco
    /// (null = o atual) e tabela, já sem crases. Partes vazias, mais de duas partes, crase sem fechar ou mal dobrada, ou
    /// qualquer coisa fora de palavra/crase (string, aspas duplas, espaço): false.
    /// </summary>
    public static bool TrySplitTableName(string target, out string? schema, out string name)
    {
        schema = null;
        name = "";
        var parts = new List<string>();
        var i = 0;
        while (true)
        {
            if (i >= target.Length) return false; // parte vazia (inclusive texto vazio ou terminando em ".")
            string part;
            if (target[i] == '`')
            {
                var sb = new System.Text.StringBuilder();
                var j = i + 1;
                var closed = false;
                while (j < target.Length)
                {
                    if (target[j] == '`')
                    {
                        if (j + 1 < target.Length && target[j + 1] == '`') { sb.Append('`'); j += 2; continue; }
                        closed = true;
                        j++;
                        break;
                    }
                    sb.Append(target[j++]);
                }
                if (!closed || sb.Length == 0) return false;
                part = sb.ToString();
                i = j;
            }
            else
            {
                var j = i;
                while (j < target.Length && (char.IsAsciiLetterOrDigit(target[j]) || target[j] is '_' or '$' || target[j] > 127)) j++;
                if (j == i) return false;
                part = target[i..j];
                i = j;
            }
            parts.Add(part);
            if (i == target.Length) break;
            if (target[i] != '.' || parts.Count == 2) return false;
            i++;
        }
        if (parts.Count == 2) schema = parts[0];
        name = parts[^1];
        return true;
    }

    /// <summary>Separa "host", "host:porta", "[v6]" ou "[v6]:porta"; IPv6 sem colchetes vale como host inteiro.</summary>
    internal static (string Host, uint Port) ParseServer(string server)
    {
        var text = (server ?? "").Trim();
        string host, portText = "";
        if (text.StartsWith('['))
        {
            var close = text.IndexOf(']');
            if (close < 0) throw new ConnectionValidationException("Servidor inválido: falta fechar o colchete do endereço IPv6.");
            host = text[1..close];
            var rest = text[(close + 1)..];
            if (rest.Length > 0)
            {
                if (rest[0] != ':') throw new ConnectionValidationException("Servidor inválido: use [endereço]:porta.");
                portText = rest[1..];
                if (portText.Length == 0) throw new ConnectionValidationException("Servidor inválido: porta vazia.");
            }
        }
        else if (text.Count(c => c == ':') > 1) host = text;
        else
        {
            var i = text.IndexOf(':');
            if (i < 0) host = text;
            else
            {
                host = text[..i];
                portText = text[(i + 1)..];
                if (portText.Length == 0) throw new ConnectionValidationException("Servidor inválido: porta vazia.");
            }
        }
        if (string.IsNullOrWhiteSpace(host)) throw new ConnectionValidationException("Informe o servidor.");
        if (portText.Length == 0) return (host, MySqlDefaultPort);
        if (!uint.TryParse(portText, out var port) || port is < 1 or > 65535)
            throw new ConnectionValidationException($"Porta inválida: '{portText}'. Use um número de 1 a 65535.");
        return (host, port);
    }

    public DbConnection CreateConnection(string connectionString) => new MySqlConnection(connectionString);

    public ConnectionFailure Translate(DbException ex) =>
        ex is MySqlException m ? Classify(m.ErrorCode, m.Message, m.InnerException) : new ConnectionFailure(ex.Message, false);

    /// <summary>Classifica o erro só com valores simples (testável sem construir MySqlException). Nunca inclui senha.</summary>
    internal static ConnectionFailure Classify(MySqlErrorCode? code, string message, Exception? inner)
    {
        if (code == MySqlErrorCode.AccessDenied)
            return new ConnectionFailure("Falha de login: usuário ou senha inválidos.", false);
        if (code is MySqlErrorCode.UnknownDatabase or MySqlErrorCode.DatabaseAccessDenied)
            return new ConnectionFailure("O banco de dados informado não existe ou o usuário não tem acesso a ele.", false);
        // Falha de TLS chega como UnableToConnectToHost ("SSL Authentication Error"): testar antes do "inalcançável".
        if (CertificateText.IsMatch(message) || HasTlsInner(inner))
            return new ConnectionFailure("O certificado do servidor não é confiável... Se o servidor é confiável, marque 'Confiar no certificado' (ou desmarque 'Criptografar a conexão'). Detalhe: " + message, true);
        if (code == MySqlErrorCode.UnableToConnectToHost || inner is SocketException)
            return new ConnectionFailure("Não foi possível alcançar o servidor (recusou a conexão, não respondeu ou o nome não existe). Verifique o nome, a porta e a rede.", false);
        return new ConnectionFailure(message, false);
    }

    private static bool HasTlsInner(Exception? e)
    {
        for (; e != null; e = e.InnerException)
            if (e is System.Security.Authentication.AuthenticationException || CertificateText.IsMatch(e.Message)) return true;
        return false;
    }

    public IDisposable SubscribeInfoMessages(DbConnection connection, Action<string> onMessage)
    {
        var my = (MySqlConnection)connection;
        MySqlInfoMessageEventHandler handler = (_, e) =>
        {
            foreach (var err in e.Errors) onMessage(err.Message);
        };
        my.InfoMessage += handler;
        return new Unsubscribe(() => my.InfoMessage -= handler);
    }

    public bool TryAttachStatementCompleted(DbCommand command, Action<long> onCompleted) => false;

    public int? ErrorNumber(DbException ex) => ex is MySqlException m ? (int)m.ErrorCode : null;

    public IEnumerable<(string Message, int? Line, bool IsError)> ErrorDetails(DbException ex, int batchFirstLine)
    {
        // O MySQL não informa a linha de forma estruturada: só quando a mensagem traz "at line N".
        int? line = null;
        var match = LineText.Match(ex.Message);
        if (match.Success && int.TryParse(match.Groups[1].Value, out var n) && n > 0) line = batchFirstLine + n - 1;
        yield return (ex.Message, line, true);
    }

    private sealed class Unsubscribe(Action action) : IDisposable
    {
        public void Dispose() => action();
    }

    /// <summary>Metadados pelo information_schema. No MySQL banco e schema são a mesma coisa: a coluna "schema" traz o banco.</summary>
    public sealed class MySqlMetadataSql : IMetadataSql
    {
        private const string SystemDbs = "('mysql','information_schema','performance_schema','sys')";

        public string ObjectsSql =>
            "SELECT TABLE_SCHEMA, TABLE_NAME, CASE TABLE_TYPE WHEN 'VIEW' THEN 'V' ELSE 'U' END FROM information_schema.TABLES " +
            $"WHERE TABLE_SCHEMA NOT IN {SystemDbs} " +
            "UNION ALL " +
            "SELECT ROUTINE_SCHEMA, ROUTINE_NAME, CASE ROUTINE_TYPE WHEN 'PROCEDURE' THEN 'P' ELSE 'FN' END FROM information_schema.ROUTINES " +
            $"WHERE ROUTINE_SCHEMA NOT IN {SystemDbs} ORDER BY 1, 2";

        // Mesmo formato do SQL Server (8 colunas; a 4ª é o tipo e a 8ª o nullable), para o leitor ser um só.
        public string ColumnsSql =>
            "SELECT TABLE_SCHEMA, TABLE_NAME, COLUMN_NAME, COLUMN_TYPE, 0, 0, 0, CASE IS_NULLABLE WHEN 'YES' THEN 1 ELSE 0 END " +
            $"FROM information_schema.COLUMNS WHERE TABLE_SCHEMA NOT IN {SystemDbs} ORDER BY TABLE_SCHEMA, TABLE_NAME, ORDINAL_POSITION";

        public string ObjectKind(string rawType) => MetadataReader.ObjectKind(rawType);

        /// <summary>COLUMN_TYPE já vem pronto: <c>varchar(50)</c>, <c>decimal(10,2)</c>, <c>tinyint(1)</c>.</summary>
        public string FormatType(DbDataReader row) => row.GetString(3);

        public bool HasSchemaLevel => false;
    }

    private sealed class MySqlTransactionSql : ITransactionSql
    {
        public string? OpenCountSql => null; // não há @@TRANCOUNT: o SessionDb usa a MySqlTransactionProbe
        public string BeginSql => "START TRANSACTION";
        public string CommitAllSql => "COMMIT";
        public string RollbackAllSql => "ROLLBACK";
        public string SavepointSql(string name) => $"SAVEPOINT {name}";
        public string RollbackToSavepointSql(string name) => $"ROLLBACK TO SAVEPOINT {name}";
        public string? ReleaseSavepointSql(string name) => $"RELEASE SAVEPOINT {name}";
        public int? SavepointMissingError => 1305;
        public string CountRowsSql(string quotedTable) => $"SELECT COUNT(*) FROM {quotedTable}";

        public string? TableEngineSql =>
            "SELECT TABLE_SCHEMA, TABLE_NAME, ENGINE FROM information_schema.TABLES " +
            "WHERE TABLE_SCHEMA = COALESCE(@schema, DATABASE()) AND TABLE_NAME = @name";

        /// <summary>
        /// Só InnoDB desfaz com ROLLBACK. MyISAM, Aria (segura contra queda, mas sem transação), MEMORY, CSV, ARCHIVE...
        /// não; view (ENGINE nulo) grava nas tabelas de base, que podem ser de qualquer mecanismo: na dúvida, não.
        /// </summary>
        public bool IsTransactionalEngine(string? engine) => string.Equals(engine, "InnoDB", StringComparison.OrdinalIgnoreCase);

        public int? RollbackIncompleteWarning => 1196;
    }
}
