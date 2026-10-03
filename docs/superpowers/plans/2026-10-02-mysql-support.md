# Suporte a MySQL e MariaDB Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Permitir conectar, executar, cancelar, navegar metadados, exportar e usar as travas de segurança em MySQL 8+ e MariaDB, sem mudar o comportamento do SQL Server.

**Architecture:** Uma interface `IDatabaseProvider` (Core) esconde tudo que depende do banco: criar conexão, connection string, tradução de erros, SQL de transação/savepoint, consultas de metadados e o `ISqlAnalyzer` (SqlAnalysis). `SqlServerProvider` embrulha o código atual; `MySqlProvider` usa MySqlConnector e um analisador léxico próprio. Core e Host passam a usar `DbConnection`/`DbException` e escolhem o provedor pela conexão da aba.

**Tech Stack:** .NET 8, xUnit, MySqlConnector, Microsoft.Data.SqlClient, ScriptDom (só SQL Server), React/TypeScript/vitest, Docker (MySQL 8 e MariaDB para teste real).

**Spec:** `docs/superpowers/specs/2026-10-02-mysql-support-design.md`

## Global Constraints

- Git: commits **sem** linha de coautoria; mensagens em pt-BR no estilo do histórico (frase no passado/presente descritiva).
- Textos de interface, mensagens e comentários em pt-BR.
- Não regredir o SQL Server: os 358 testes .NET e 124 vitest atuais passam ao fim de **cada** task (`dotnet test SqlDesk.sln` e `npm test` em `src/SqlDesk.Web`).
- Senhas nunca são gravadas em arquivo, log, commit ou na memória; a senha dos containers de teste é descartável e só existe no `docker run`/compose local.
- Só autenticação por usuário e senha. Sem SSH tunnel, sem Windows auth, sem PostgreSQL.
- O C# decide a segurança; o frontend só exibe diálogos. Toda execução passa pelo analisador antes de chegar ao banco. Na dúvida, o analisador do MySQL trata como perigoso.
- Conexões salvas sem o campo `provider` continuam valendo como SQL Server.
- Imagem Docker autorizada pelo usuário: `mysql:8` e `mariadb:11`. Não baixar outras.
- Desvio consciente da spec §2: o contrato de conexão **mantém** `encrypt`/`trustServerCertificate` e o MySQL os mapeia para `SslMode` (ver Task 7); a interface mostra rótulos próprios do MySQL. Evita mudar o contrato e as conexões salvas.

## Review Focus

- Script MySQL com `;` ou `UPDATE` dentro de string, comentário (`-- `, `#`, `/* */`) ou crase: não pode virar falso statement nem esconder um perigo. (Task 4)
- `UPDATE`/`DELETE` sem WHERE em MySQL com `LIMIT` ou com `WHERE 1=1`: `LIMIT` não torna o comando seguro nem perigoso por si, `WHERE 1=1` é "não filtra" e precisa travar. (Task 4)
- DDL (DROP/TRUNCATE/ALTER) no MySQL faz commit implícito: o app não pode oferecer "rollback" que não funciona. (Task 9)
- Aba MySQL sem permissão para saber se há transação aberta (sem `performance_schema`): o app não pode afirmar "sem transação" por engano. (Task 8)
- Conexão MySQL com senha com `;`, `=`, aspas ou espaços na connection string. (Task 7)
- Valores MySQL `0000-00-00`, `TINYINT(1)`, `BIT`, `JSON`, `BLOB` grande e `DECIMAL(65,30)` na grade e na exportação sem exceção. (Task 11)

---

### Task 0: Branch, baseline e containers de teste

**Files:**
- Create: `tests/docker/docker-compose.yml`
- Create: `tests/docker/README.md`

**Interfaces:**
- Produces: MySQL 8 em `127.0.0.1:33306` e MariaDB em `127.0.0.1:33307`, banco `sqldesk_test`, usuário `sqldesk`. A senha vem da variável de ambiente `SQLDESK_TEST_PWD` (definida pelo usuário na sessão; nunca no arquivo).

- [ ] **Step 1: Confirmar a branch e o baseline verde**

Run: `git branch --show-current && dotnet test SqlDesk.sln --nologo -v q && (cd src/SqlDesk.Web && npm test --silent)`
Expected: branch `feature/mysql`; todos os testes passam.

- [ ] **Step 2: Criar o compose (senha por variável, sem valor no arquivo)**

```yaml
# tests/docker/docker-compose.yml
services:
  mysql:
    image: mysql:8
    ports: ["127.0.0.1:33306:3306"]
    environment:
      MYSQL_ROOT_PASSWORD: ${SQLDESK_TEST_PWD:?defina SQLDESK_TEST_PWD}
      MYSQL_DATABASE: sqldesk_test
      MYSQL_USER: sqldesk
      MYSQL_PASSWORD: ${SQLDESK_TEST_PWD}
    tmpfs: ["/var/lib/mysql"]
  mariadb:
    image: mariadb:11
    ports: ["127.0.0.1:33307:3306"]
    environment:
      MARIADB_ROOT_PASSWORD: ${SQLDESK_TEST_PWD:?defina SQLDESK_TEST_PWD}
      MARIADB_DATABASE: sqldesk_test
      MARIADB_USER: sqldesk
      MARIADB_PASSWORD: ${SQLDESK_TEST_PWD}
    tmpfs: ["/var/lib/mysql"]
```

```markdown
<!-- tests/docker/README.md -->
# Bancos de teste (MySQL e MariaDB)

Containers descartáveis (dados em tmpfs) só para os testes de integração.

    $env:SQLDESK_TEST_PWD = "<senha descartável>"   # PowerShell; não grave em arquivo
    docker compose -f tests/docker/docker-compose.yml up -d
    dotnet test tests/SqlDesk.Integration.Tests
    docker compose -f tests/docker/docker-compose.yml down
```

- [ ] **Step 3: Subir os containers e esperar ficarem prontos**

Run: `docker compose -f tests/docker/docker-compose.yml up -d` (com `SQLDESK_TEST_PWD` definida) e depois `docker compose -f tests/docker/docker-compose.yml logs --tail 3 mysql mariadb`
Expected: os dois logs terminam com "ready for connections".

- [ ] **Step 4: Commit**

```bash
git add tests/docker
git commit -m "Adiciona containers descartáveis de MySQL e MariaDB para os testes de integração"
```

---

### Task 1: Campo `Provider` na conexão, compatível com o que já está salvo

**Files:**
- Create: `src/SqlDesk.Core/Providers/ProviderIds.cs`
- Modify: `src/SqlDesk.Core/Connections/Models.cs`
- Test: `tests/SqlDesk.Core.Tests/ConnectionTests.cs`

**Interfaces:**
- Produces: `ProviderIds.SqlServer = "sqlserver"`, `ProviderIds.MySql = "mysql"`; `ConnectionSettings.Provider` (string, `init`, padrão `"sqlserver"`).

- [ ] **Step 1: Teste que falha (JSON antigo sem `provider`; ida e volta do store)**

```csharp
// tests/SqlDesk.Core.Tests/ConnectionTests.cs (nova classe no fim do arquivo)
public class ConnectionProviderFieldTests
{
    [Fact]
    public void Json_antigo_sem_provider_vira_sqlserver()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        var path = Path.Combine(dir, "connections.json");
        var id = Guid.NewGuid();
        File.WriteAllText(path, $$"""
        {"version":1,"connections":[{"id":"{{id}}","name":"Velha","color":"#0078D4",
         "settings":{"server":"srv","database":"db","user":"u","connectTimeout":15,"commandTimeout":30,
                     "encrypt":true,"trustServerCertificate":false,"advanced":{}}}]}
        """);
        var store = new ConnectionStore(path, new NoopProtector());
        Assert.Equal(ProviderIds.SqlServer, store.Get(id)!.Settings.Provider);
    }

    [Fact]
    public void Provider_mysql_sobrevive_ao_salvar_e_ler()
    {
        var path = Path.Combine(Directory.CreateTempSubdirectory().FullName, "c.json");
        var store = new ConnectionStore(path, new NoopProtector());
        var saved = store.Save(new SaveConnectionRequest(null, "My", "#112233",
            new ConnectionSettings("h", "d", "u") { Provider = ProviderIds.MySql }, null));
        Assert.Equal(ProviderIds.MySql, new ConnectionStore(path, new NoopProtector()).Get(saved.Id)!.Settings.Provider);
    }

    private sealed class NoopProtector : IPasswordProtector
    {
        public string Protect(string plain) => plain;
        public string Unprotect(string protectedText) => protectedText;
    }
}
```

Antes de rodar, abrir `src/SqlDesk.Core/Connections/PasswordProtector.cs` e conferir os nomes dos métodos de `IPasswordProtector`; ajustar `NoopProtector` se diferirem.

- [ ] **Step 2: Rodar e ver falhar**

Run: `dotnet test tests/SqlDesk.Core.Tests --filter ConnectionProviderFieldTests --nologo -v q`
Expected: erro de compilação (`ProviderIds`/`Provider` não existem).

- [ ] **Step 3: Implementar**

```csharp
// src/SqlDesk.Core/Providers/ProviderIds.cs
namespace SqlDesk.Core.Providers;

/// <summary>Identificadores estáveis dos bancos suportados (gravados em connections.json e trafegados na ponte).</summary>
public static class ProviderIds
{
    public const string SqlServer = "sqlserver";
    public const string MySql = "mysql";

    public static bool IsKnown(string? id) => id is SqlServer or MySql;
}
```

Em `Models.cs`, dentro do corpo do record `ConnectionSettings` (junto de `Advanced`), adicionar:

```csharp
    /// <summary>Banco da conexão. Ausente em conexões salvas antes do MySQL: vale SQL Server.</summary>
    public string Provider { get; init; } = SqlDesk.Core.Providers.ProviderIds.SqlServer;
```

E no `Validate` de `ConnectionStore.cs` acrescentar:

```csharp
        if (!SqlDesk.Core.Providers.ProviderIds.IsKnown(r.Settings.Provider)) throw new ConnectionValidationException("Tipo de servidor desconhecido.");
```

- [ ] **Step 4: Rodar tudo e ver passar**

Run: `dotnet test SqlDesk.sln --nologo -v q`
Expected: tudo passa.

- [ ] **Step 5: Commit**

```bash
git add src/SqlDesk.Core tests/SqlDesk.Core.Tests
git commit -m "Adiciona o campo de provedor às conexões, tratando as salvas antes como SQL Server"
```

---

### Task 2: `ISqlAnalyzer` e o adaptador do SQL Server

**Files:**
- Create: `src/SqlDesk.SqlAnalysis/ISqlAnalyzer.cs`
- Create: `src/SqlDesk.SqlAnalysis/SqlServerAnalyzer.cs`
- Test: `tests/SqlDesk.SqlAnalysis.Tests/SqlServerAnalyzerTests.cs`

**Interfaces:**
- Produces:
```csharp
public interface ISqlAnalyzer
{
    ScriptAnalysis Analyze(string script);
    LocateResult Locate(string text, int cursor);
    RewriteResult Rewrite(string script);          // reescrita para pré-visualização (OUTPUT); MySQL devolve o texto original
    bool IsReadOnly(string script, out string? reason);
    string StatementSeparatorHint { get; }          // texto para o aviso de "GO n" (SQL Server: "GO"); MySQL: ""
}
public sealed class SqlServerAnalyzer : ISqlAnalyzer { public static readonly SqlServerAnalyzer Instance; }
```

- [ ] **Step 1: Teste que falha**

```csharp
// tests/SqlDesk.SqlAnalysis.Tests/SqlServerAnalyzerTests.cs
using SqlDesk.SqlAnalysis;

namespace SqlDesk.SqlAnalysis.Tests;

public class SqlServerAnalyzerTests
{
    private static readonly ISqlAnalyzer A = SqlServerAnalyzer.Instance;

    [Fact]
    public void Delega_para_o_analisador_atual()
    {
        Assert.False(A.Analyze("DELETE FROM t").IsSafe);
        Assert.True(A.Analyze("SELECT 1").IsSafe);
        Assert.True(A.IsReadOnly("SELECT 1", out _));
        Assert.False(A.IsReadOnly("DELETE FROM t", out var reason));
        Assert.NotNull(reason);
        Assert.True(A.Locate("SELECT 1", 3).Found);
        Assert.Single(A.Rewrite("DELETE FROM t").Rewritten);
    }
}
```

- [ ] **Step 2: Rodar e ver falhar**

Run: `dotnet test tests/SqlDesk.SqlAnalysis.Tests --filter SqlServerAnalyzerTests --nologo -v q`
Expected: erro de compilação.

- [ ] **Step 3: Implementar**

```csharp
// src/SqlDesk.SqlAnalysis/ISqlAnalyzer.cs
namespace SqlDesk.SqlAnalysis;

/// <summary>Análise de SQL específica de um banco. Quem executa pergunta ao analisador do provedor da aba.</summary>
public interface ISqlAnalyzer
{
    ScriptAnalysis Analyze(string script);

    LocateResult Locate(string text, int cursor);

    /// <summary>Reescreve UPDATE/DELETE perigosos para devolver as linhas afetadas. Sem esse recurso, devolve o texto como veio.</summary>
    RewriteResult Rewrite(string script);

    bool IsReadOnly(string script, out string? reason);
}
```

```csharp
// src/SqlDesk.SqlAnalysis/SqlServerAnalyzer.cs
namespace SqlDesk.SqlAnalysis;

/// <summary>T-SQL: delega para o ScriptDom (código que já existia).</summary>
public sealed class SqlServerAnalyzer : ISqlAnalyzer
{
    public static readonly SqlServerAnalyzer Instance = new();

    public ScriptAnalysis Analyze(string script) => SqlScriptAnalyzer.Analyze(script);

    public LocateResult Locate(string text, int cursor) => StatementLocator.Locate(text, cursor);

    public RewriteResult Rewrite(string script) => OutputRewriter.Rewrite(script);

    public bool IsReadOnly(string script, out string? reason) => ReadOnlyAnalyzer.IsReadOnly(script, out reason);
}
```

(Removi `StatementSeparatorHint` da interface: a recusa de `GO n` fica no `ExecutionPlanner`, que só a aplica quando o lote veio de `GO`; o MySQL nunca produz `RepeatCount > 1`.)

- [ ] **Step 4: Rodar e ver passar; suíte toda**

Run: `dotnet test SqlDesk.sln --nologo -v q`
Expected: passa.

- [ ] **Step 5: Commit**

```bash
git add src/SqlDesk.SqlAnalysis tests/SqlDesk.SqlAnalysis.Tests
git commit -m "Introduz a interface do analisador de SQL e o adaptador do SQL Server"
```

---

### Task 3: `ExecutionPlanner` e exportação recebem o analisador

**Files:**
- Modify: `src/SqlDesk.Core/Execution/ExecutionPlanner.cs` (assinatura de `Plan`, linhas 45-75)
- Modify: `src/SqlDesk.Core/Export/ExportService.cs:119-121`
- Modify: `src/SqlDesk.Host/Handlers/ExecutionHandlers.cs:69`
- Test: `tests/SqlDesk.Core.Tests/ExecutionPlannerTests.cs`

**Interfaces:**
- Consumes: `ISqlAnalyzer` (Task 2).
- Produces: `ExecutionPlanner.Plan(ISqlAnalyzer analyzer, string text, int cursor, int selectionStart, int selectionEnd, bool wholeScript)`; `ExportService.ExportByRerunAsync(..., ISqlAnalyzer analyzer, ...)` com `analyzer` logo após `sourceText`.

Neste task o Host ainda usa `SqlServerAnalyzer.Instance`; a escolha por aba vem na Task 6.

- [ ] **Step 1: Atualizar os testes existentes e adicionar um que prova o uso do analisador**

Em `ExecutionPlannerTests.cs` e onde mais houver chamada, trocar `ExecutionPlanner.Plan(` por `ExecutionPlanner.Plan(SqlServerAnalyzer.Instance, ` (buscar com `grep -rn "ExecutionPlanner.Plan(" tests src`). Adicionar:

```csharp
    private sealed class DangerAlwaysAnalyzer : ISqlAnalyzer
    {
        public ScriptAnalysis Analyze(string script) => new(
            [new Batch(0, script, 0, 1)],
            [new DangerousStatement(DangerKind.Unanalyzable, 0, script.Length, 1, "x", null, [], false)], [], []);
        public LocateResult Locate(string text, int cursor) => new(new TextRange(0, text.Length), null, false);
        public RewriteResult Rewrite(string script) => new([], [], []);
        public bool IsReadOnly(string script, out string? reason) { reason = null; return true; }
    }

    [Fact]
    public void Usa_o_analisador_recebido()
    {
        var plan = ExecutionPlanner.Plan(new DangerAlwaysAnalyzer(), "SELECT 1", 0, 0, 0, wholeScript: true);
        Assert.IsType<ExecutionPlan.Dangerous>(plan);
    }
```

- [ ] **Step 2: Rodar e ver falhar** (`dotnet test tests/SqlDesk.Core.Tests --nologo -v q`; erro de compilação na assinatura).

- [ ] **Step 3: Implementar**

Em `ExecutionPlanner.Plan`: novo primeiro parâmetro `ISqlAnalyzer analyzer`; trocar `StatementLocator.Locate(` por `analyzer.Locate(` e `SqlScriptAnalyzer.Analyze(sub)` por `analyzer.Analyze(sub)`. Em `ExportService.ExportByRerunAsync`: novo parâmetro `ISqlAnalyzer analyzer` depois de `sourceText`; usar `ExecutionPlanner.Plan(analyzer, sourceText, ...)` e `analyzer.IsReadOnly(sourceText, out var reason)`. Em `ExportHandlers.cs` (chamador de `ExportByRerunAsync`) e `ExecutionHandlers.cs:69` passar `SqlDesk.SqlAnalysis.SqlServerAnalyzer.Instance` por enquanto. Ajustar os testes de `ExportServiceTests.cs` que chamam `ExportByRerunAsync` do mesmo jeito.

- [ ] **Step 4: Rodar a suíte inteira.** Expected: passa.

- [ ] **Step 5: Commit**

```bash
git add -A src tests
git commit -m "Faz o planejador de execução e a reexecução da exportação usarem o analisador recebido"
```

---

### Task 4: Analisador léxico do MySQL (o núcleo de segurança)

**Files:**
- Create: `src/SqlDesk.SqlAnalysis/MySql/MySqlScanner.cs`
- Create: `src/SqlDesk.SqlAnalysis/MySql/MySqlAnalyzer.cs`
- Modify: `src/SqlDesk.SqlAnalysis/Models.cs` (adicionar `DangerKind.AlterTable`)
- Test: `tests/SqlDesk.SqlAnalysis.Tests/MySqlAnalyzerTests.cs`

**Interfaces:**
- Produces: `MySqlAnalyzer : ISqlAnalyzer` (`MySqlAnalyzer.Instance`). Para cada statement perigoso preenche `DangerousStatement.CountQueries` com `SELECT COUNT(*) FROM <alvo>` (para UPDATE/DELETE sem WHERE, TRUNCATE e DROP TABLE), `CanPreviewWithOutput=false`. `Rewrite` devolve `RewriteResult(batches, [], dangers)` sem alterar texto. `Batch.RepeatCount` é sempre 1.

**Regras do scanner** (`MySqlScanner.Tokenize(string) : List<Token>`, `Token(Kind, Start, Length, Text)`; `Kind` ∈ Word, Quoted(string/identificador entre crase/aspas), Symbol, Semicolon):
- Comentários ignorados: `-- ` (traço-traço **seguido de espaço, tab, quebra de linha ou fim**), `#` até fim da linha, `/* */` (sem aninhar). `/*! ... */` e `/*+ ... */` são **código** (comentário executável do MySQL): seu conteúdo é tokenizado.
- Strings `'...'` e `"..."` com `\` como escape e `''`/`""` dobrados; identificadores `` `...` `` com ```` `` ```` dobrado.
- `DELIMITER xx` numa linha própria (case-insensitive) muda o terminador; a linha `DELIMITER` não vira statement. Enquanto o terminador é diferente de `;`, `;` não separa statements.
- Texto em `Word` mantém o original; comparações são sem diferença de caixa.

**Regras de perigo** (por statement, após remover comentários; primeira palavra decide):
- `UPDATE ... [WHERE ...]` e `DELETE FROM ... [WHERE ...]`: perigoso se não há `WHERE` no nível de parênteses 0, ou se o predicado "não filtra": `WHERE 1`, `WHERE 1=1`, `WHERE TRUE`, `WHERE 'x'='x'`, `WHERE 0=0`, ou o predicado não contém nenhuma palavra/identificador (só literais e operadores). `LIMIT` sozinho (sem WHERE) continua perigoso.
- `TRUNCATE [TABLE] x` → `TruncateTable`.
- `DROP TABLE|DATABASE|SCHEMA|VIEW|INDEX|PROCEDURE|FUNCTION|TRIGGER|EVENT|USER|...` (qualquer `DROP`) → `Drop`; `ALTER TABLE ... DROP COLUMN` → `DropColumn`; qualquer outro `ALTER ...` → `AlterTable`.
- Statement que não consegue ser classificado com segurança (parênteses/strings/crase sem fechar, `DELIMITER` malformado) **e** contém `UPDATE|DELETE|TRUNCATE|DROP|ALTER` como palavra → `Unanalyzable` (perigoso).
- `HasWrites` = existe `INSERT|UPDATE|DELETE|REPLACE` (a recomendação de transação é a mesma do SQL Server).
- `IsReadOnly`: só `SELECT`, `SHOW`, `DESCRIBE`/`DESC`, `EXPLAIN`, `USE`, `SET @var` e `WITH ... SELECT`; `SELECT ... INTO` e `... FOR UPDATE` não são só leitura; qualquer outra coisa é recusada.

- [ ] **Step 1: Escrever os testes que falham**

```csharp
// tests/SqlDesk.SqlAnalysis.Tests/MySqlAnalyzerTests.cs
using SqlDesk.SqlAnalysis;

namespace SqlDesk.SqlAnalysis.Tests;

public class MySqlAnalyzerTests
{
    private static readonly ISqlAnalyzer A = MySqlAnalyzer.Instance;

    private static DangerKind[] Kinds(string sql) => A.Analyze(sql).Dangers.Select(d => d.Kind).ToArray();

    [Theory]
    [InlineData("UPDATE t SET a = 1", DangerKind.UpdateWithoutWhere)]
    [InlineData("update `t` set a=1 limit 10", DangerKind.UpdateWithoutWhere)]
    [InlineData("DELETE FROM t", DangerKind.DeleteWithoutWhere)]
    [InlineData("DELETE FROM db.t WHERE 1=1", DangerKind.DeleteWithoutWhere)]
    [InlineData("DELETE FROM t WHERE TRUE", DangerKind.DeleteWithoutWhere)]
    [InlineData("UPDATE t SET a=1 WHERE 'x'='x'", DangerKind.UpdateWithoutWhere)]
    [InlineData("TRUNCATE TABLE t", DangerKind.TruncateTable)]
    [InlineData("TRUNCATE t", DangerKind.TruncateTable)]
    [InlineData("DROP TABLE t", DangerKind.Drop)]
    [InlineData("DROP DATABASE d", DangerKind.Drop)]
    [InlineData("ALTER TABLE t DROP COLUMN c", DangerKind.DropColumn)]
    [InlineData("ALTER TABLE t ADD COLUMN c INT", DangerKind.AlterTable)]
    public void Detecta_perigosos(string sql, DangerKind kind) => Assert.Equal([kind], Kinds(sql));

    [Theory]
    [InlineData("SELECT * FROM t")]
    [InlineData("UPDATE t SET a=1 WHERE id = 5")]
    [InlineData("DELETE FROM t WHERE id IN (1,2)")]
    [InlineData("INSERT INTO t VALUES (1)")]
    [InlineData("SELECT 'DELETE FROM t'")]
    [InlineData("SELECT 1 -- DELETE FROM t")]
    [InlineData("SELECT 1 # DROP TABLE t")]
    [InlineData("SELECT /* TRUNCATE t */ 1")]
    [InlineData("SELECT `drop` FROM t")]
    [InlineData("UPDATE t SET a='a; DELETE FROM x' WHERE id=1")]
    public void Nao_acusa_o_que_e_seguro(string sql) => Assert.Empty(Kinds(sql));

    [Fact]
    public void Ponto_e_virgula_em_string_nao_separa_e_nao_esconde_perigo()
    {
        var a = A.Analyze("SELECT ';'; DELETE FROM t; SELECT 2");
        Assert.Single(a.Dangers);
        Assert.Equal(DangerKind.DeleteWithoutWhere, a.Dangers[0].Kind);
        Assert.Equal(3, a.Batches.Count);
    }

    [Fact]
    public void Comentario_executavel_conta_como_codigo() =>
        Assert.Equal([DangerKind.DeleteWithoutWhere], Kinds("/*! DELETE FROM t */"));

    [Fact]
    public void Traco_traco_sem_espaco_nao_e_comentario() =>
        // "--1" em MySQL é "- -1": o resto da linha é código.
        Assert.Equal([DangerKind.DeleteWithoutWhere], Kinds("SELECT 1 --1\n; DELETE FROM t"));

    [Fact]
    public void Delimiter_muda_o_terminador()
    {
        var sql = "DELIMITER $$\nCREATE PROCEDURE p() BEGIN DELETE FROM t WHERE id=1; END$$\nDELIMITER ;\nSELECT 1;";
        var a = A.Analyze(sql);
        Assert.Empty(a.Dangers);
        Assert.Equal(2, a.Batches.Count);
    }

    [Fact]
    public void Trecho_nao_analisavel_com_palavra_destrutiva_e_perigoso() =>
        Assert.Equal([DangerKind.Unanalyzable], Kinds("DELETE FROM t WHERE a = 'aberta"));

    [Fact]
    public void Gera_contagem_previa()
    {
        var d = A.Analyze("DELETE FROM `db`.`t`").Dangers.Single();
        Assert.Equal(["SELECT COUNT(*) FROM `db`.`t`"], d.CountQueries);
        Assert.False(d.CanPreviewWithOutput);
    }

    [Fact]
    public void Marca_escrita_com_where() => Assert.True(A.Analyze("UPDATE t SET a=1 WHERE id=1").HasWrites);

    [Fact]
    public void Localiza_statement_sob_o_cursor()
    {
        const string sql = "SELECT 1;\nSELECT ';' AS x;\nSELECT 3";
        var r = A.Locate(sql, sql.IndexOf("AS x", StringComparison.Ordinal));
        Assert.Equal("SELECT ';' AS x", sql.Substring(r.Range!.Start, r.Range.Length));
    }

    [Fact]
    public void Localiza_em_linha_em_branco_nao_acha()
    {
        var r = A.Locate("SELECT 1;\n\nSELECT 2", 10);
        Assert.False(r.Found);
    }

    [Theory]
    [InlineData("SELECT 1", true)]
    [InlineData("SHOW TABLES", true)]
    [InlineData("DESCRIBE t", true)]
    [InlineData("EXPLAIN SELECT 1", true)]
    [InlineData("WITH c AS (SELECT 1) SELECT * FROM c", true)]
    [InlineData("SELECT * FROM t INTO OUTFILE 'x'", false)]
    [InlineData("SELECT * FROM t FOR UPDATE", false)]
    [InlineData("INSERT INTO t VALUES (1)", false)]
    [InlineData("CALL p()", false)]
    public void Somente_leitura(string sql, bool expected) => Assert.Equal(expected, A.IsReadOnly(sql, out _));

    [Fact]
    public void Rewrite_nao_altera_o_texto()
    {
        var r = A.Rewrite("DELETE FROM t");
        Assert.Empty(r.Rewritten);
        Assert.Single(r.NotRewritten);
        Assert.Equal("DELETE FROM t", r.Batches.Single().Text);
    }
}
```

- [ ] **Step 2: Rodar e ver falhar** (`dotnet test tests/SqlDesk.SqlAnalysis.Tests --filter MySqlAnalyzerTests --nologo -v q`; erro de compilação).

- [ ] **Step 3: Implementar o scanner**

```csharp
// src/SqlDesk.SqlAnalysis/MySql/MySqlScanner.cs
using System.Text;

namespace SqlDesk.SqlAnalysis.MySql;

internal enum TokenKind { Word, Quoted, Symbol, Terminator }

/// <summary><paramref name="Text"/> é o texto original; para Quoted inclui as aspas/crases.</summary>
internal sealed record Token(TokenKind Kind, int Start, int Length, string Text)
{
    public int End => Start + Length;

    public bool Is(string word) => Kind == TokenKind.Word && Text.Equals(word, StringComparison.OrdinalIgnoreCase);
}

/// <summary>Resultado da varredura: tokens de código, statements (listas de tokens) e se houve erro léxico (string/crase/bloco abertos).</summary>
internal sealed record ScanResult(IReadOnlyList<Token> Tokens, IReadOnlyList<MySqlStatement> Statements, bool Broken);

internal sealed record MySqlStatement(int Start, int End, IReadOnlyList<Token> Tokens, bool Broken);

/// <summary>
/// Varredura léxica de MySQL/MariaDB: sabe o que é código, string, identificador entre crase e comentário, e entende
/// <c>DELIMITER</c>. Não é um parser: serve para separar statements sem se enganar com <c>;</c> dentro de strings e comentários.
/// </summary>
internal static class MySqlScanner
{
    public static ScanResult Scan(string text)
    {
        var statements = new List<MySqlStatement>();
        var all = new List<Token>();
        var current = new List<Token>();
        var delimiter = ";";
        var broken = false;
        var stmtBroken = false;
        var stmtStart = -1;
        var i = 0;

        void Flush(int end)
        {
            if (current.Count > 0)
                statements.Add(new MySqlStatement(current[0].Start, Math.Max(end, current[^1].End), [.. current], stmtBroken));
            current = [];
            stmtBroken = false;
            stmtStart = -1;
        }

        while (i < text.Length)
        {
            var c = text[i];

            if (char.IsWhiteSpace(c)) { i++; continue; }

            // DELIMITER xx numa linha própria, só entre statements.
            if (current.Count == 0 && IsDelimiterLine(text, i, out var newDelimiter, out var lineEnd))
            {
                delimiter = newDelimiter;
                i = lineEnd;
                continue;
            }

            // Comentários
            if (c == '#') { i = SkipLine(text, i); continue; }
            if (c == '-' && i + 1 < text.Length && text[i + 1] == '-' && (i + 2 >= text.Length || char.IsWhiteSpace(text[i + 2])))
            { i = SkipLine(text, i); continue; }
            if (c == '/' && i + 1 < text.Length && text[i + 1] == '*')
            {
                var executable = i + 2 < text.Length && (text[i + 2] == '!' || text[i + 2] == '+');
                var close = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                if (close < 0) { if (!executable) { broken = stmtBroken = true; i = text.Length; continue; } close = text.Length - 2; broken = stmtBroken = true; }
                if (!executable) { i = close + 2; continue; }
                // /*! ... */ e /*+ ... */: o conteúdo é código. Pula "/*!" + versão opcional e tokeniza o miolo recursivamente.
                var innerStart = i + 3;
                while (innerStart < close && char.IsDigit(text[innerStart])) innerStart++;
                var inner = Scan(text[innerStart..close]);
                foreach (var st in inner.Statements)
                    foreach (var t in st.Tokens)
                    {
                        var shifted = t with { Start = t.Start + innerStart };
                        current.Add(shifted); all.Add(shifted);
                    }
                broken |= inner.Broken; stmtBroken |= inner.Broken;
                if (stmtStart < 0) stmtStart = i;
                i = close + 2;
                continue;
            }

            // Terminador atual (pode ter mais de um caractere)
            if (string.CompareOrdinal(text, i, delimiter, 0, delimiter.Length) == 0)
            {
                var term = new Token(TokenKind.Terminator, i, delimiter.Length, delimiter);
                all.Add(term);
                Flush(i);
                i += delimiter.Length;
                continue;
            }

            Token tok;
            if (c == '\'' || c == '"' || c == '`')
            {
                var end = ReadQuoted(text, i, c, out var closed);
                if (!closed) broken = stmtBroken = true;
                tok = new Token(TokenKind.Quoted, i, end - i, text[i..end]);
                i = end;
            }
            else if (char.IsLetterOrDigit(c) || c == '_' || c == '$' || c == '@' || c > 127)
            {
                var j = i + 1;
                while (j < text.Length && (char.IsLetterOrDigit(text[j]) || text[j] == '_' || text[j] == '$' || text[j] == '@' || text[j] > 127)) j++;
                tok = new Token(TokenKind.Word, i, j - i, text[i..j]);
                i = j;
            }
            else
            {
                tok = new Token(TokenKind.Symbol, i, 1, c.ToString());
                i++;
            }

            if (stmtStart < 0) stmtStart = tok.Start;
            current.Add(tok);
            all.Add(tok);
        }

        Flush(text.Length);
        return new ScanResult(all, statements, broken);
    }

    private static int SkipLine(string t, int i)
    {
        while (i < t.Length && t[i] != '\n' && t[i] != '\r') i++;
        return i;
    }

    private static int ReadQuoted(string t, int start, char quote, out bool closed)
    {
        var i = start + 1;
        while (i < t.Length)
        {
            if (t[i] == '\\' && quote != '`') { i += 2; continue; }
            if (t[i] == quote)
            {
                if (i + 1 < t.Length && t[i + 1] == quote) { i += 2; continue; }
                closed = true;
                return i + 1;
            }
            i++;
        }
        closed = false;
        return t.Length;
    }

    private static bool IsDelimiterLine(string t, int i, out string delimiter, out int lineEnd)
    {
        delimiter = ";";
        lineEnd = i;
        const string kw = "DELIMITER";
        if (i + kw.Length >= t.Length || string.Compare(t, i, kw, 0, kw.Length, StringComparison.OrdinalIgnoreCase) != 0) return false;
        // Precisa estar no início da linha (só espaço antes) e ter espaço depois da palavra.
        var ls = i;
        while (ls > 0 && t[ls - 1] != '\n' && t[ls - 1] != '\r') { if (!char.IsWhiteSpace(t[ls - 1])) return false; ls--; }
        var j = i + kw.Length;
        if (j >= t.Length || (t[j] != ' ' && t[j] != '\t')) return false;
        while (j < t.Length && (t[j] == ' ' || t[j] == '\t')) j++;
        var s = j;
        while (j < t.Length && t[j] != '\n' && t[j] != '\r') j++;
        var d = t[s..j].Trim();
        if (d.Length == 0) return false;
        delimiter = d;
        lineEnd = j;
        return true;
    }
}
```

- [ ] **Step 4: Implementar o analisador**

```csharp
// src/SqlDesk.SqlAnalysis/MySql/MySqlAnalyzer.cs
using SqlDesk.SqlAnalysis.MySql;

namespace SqlDesk.SqlAnalysis;

/// <summary>
/// Analisador léxico para MySQL/MariaDB (não existe parser equivalente ao ScriptDom). Erra para o lado seguro:
/// o que não dá para classificar e contém comando destrutivo é tratado como perigoso.
/// </summary>
public sealed class MySqlAnalyzer : ISqlAnalyzer
{
    public static readonly MySqlAnalyzer Instance = new();

    private static readonly string[] Destructive = ["UPDATE", "DELETE", "TRUNCATE", "DROP", "ALTER"];

    public ScriptAnalysis Analyze(string script)
    {
        var scan = MySqlScanner.Scan(script);
        var map = new LineMap(script);
        var batches = new List<Batch>();
        var dangers = new List<DangerousStatement>();
        var writes = false;

        foreach (var st in scan.Statements)
        {
            var text = script[st.Start..st.End];
            batches.Add(new Batch(batches.Count, text, st.Start, map.LineOf(st.Start)));
            writes |= HasWrite(st);
            if (Classify(st, script, map) is { } d) dangers.Add(d);
        }
        return new ScriptAnalysis(batches, dangers.OrderBy(d => d.Start).ToList(), [], [], writes);
    }

    public LocateResult Locate(string text, int cursor)
    {
        cursor = Math.Clamp(cursor, 0, text.Length);
        var scan = MySqlScanner.Scan(text);
        var hit = scan.Statements.LastOrDefault(s => cursor >= s.Start && cursor <= s.End)
                  // cursor logo após o ';' do statement anterior ou na mesma linha dele
                  ?? scan.Statements.LastOrDefault(s => s.End <= cursor && text.AsSpan(s.End, cursor - s.End).IndexOfAny('\n', '\r') < 0
                      && text.AsSpan(s.End, cursor - s.End).Trim().Trim(';').Length == 0);
        if (hit is null)
        {
            var blank = LineIsBlank(text, cursor);
            return new LocateResult(null, blank
                ? "O cursor está numa linha em branco; posicione-o sobre um statement."
                : "Não há nenhum statement sob o cursor.", false);
        }
        return new LocateResult(new TextRange(hit.Start, hit.End - hit.Start), null, false);
    }

    public RewriteResult Rewrite(string script)
    {
        var a = Analyze(script);
        return new RewriteResult(a.Batches, [], a.Dangers);
    }

    public bool IsReadOnly(string script, out string? reason)
    {
        reason = null;
        var scan = MySqlScanner.Scan(script);
        if (scan.Broken) { reason = "o texto não pôde ser analisado (string, crase ou comentário sem fechar)"; return false; }
        foreach (var st in scan.Statements)
        {
            var first = st.Tokens[0];
            var ok = first.Is("SELECT") || first.Is("SHOW") || first.Is("DESCRIBE") || first.Is("DESC") || first.Is("EXPLAIN") ||
                     first.Is("USE") || first.Is("WITH") || (first.Is("SET") && st.Tokens.Count > 1 && st.Tokens[1].Text.StartsWith('@'));
            if (!ok) { reason = $"contém {first.Text.ToUpperInvariant()}, que não é só leitura"; return false; }
            for (var i = 0; i < st.Tokens.Count; i++)
            {
                var t = st.Tokens[i];
                if (t.Is("INTO") && st.Tokens.Any(x => x.Is("SELECT"))) { reason = "contém SELECT ... INTO"; return false; }
                if (t.Is("FOR") && i + 1 < st.Tokens.Count && (st.Tokens[i + 1].Is("UPDATE") || st.Tokens[i + 1].Is("SHARE"))) { reason = "contém FOR UPDATE/SHARE (trava linhas)"; return false; }
                if (first.Is("WITH") && (t.Is("UPDATE") || t.Is("DELETE") || t.Is("INSERT"))) { reason = $"contém {t.Text.ToUpperInvariant()}"; return false; }
            }
        }
        return true;
    }

    private static bool HasWrite(MySqlStatement st) =>
        st.Tokens.Count > 0 && (st.Tokens[0].Is("INSERT") || st.Tokens[0].Is("UPDATE") || st.Tokens[0].Is("DELETE") || st.Tokens[0].Is("REPLACE"));

    private static DangerousStatement? Classify(MySqlStatement st, string script, LineMap map)
    {
        var t = st.Tokens;
        if (t.Count == 0) return null;
        var line = map.LineOf(st.Start);
        var length = Math.Max(1, st.End - st.Start);

        DangerousStatement D(DangerKind kind, string? target, string why, IReadOnlyList<string>? counts = null) =>
            new(kind, st.Start, length, line, why, target, counts ?? [], CanPreviewWithOutput: false);

        var first = t[0];

        if (st.Broken)
        {
            var kw = t.FirstOrDefault(x => Destructive.Any(x.Is));
            return kw is null ? null : D(DangerKind.Unanalyzable, null,
                $"Não foi possível analisar este trecho (string, crase ou comentário sem fechar) e ele contém {kw.Text.ToUpperInvariant()}; tratado como perigoso por segurança");
        }

        if (first.Is("UPDATE") || first.Is("DELETE"))
        {
            var isUpdate = first.Is("UPDATE");
            var target = TargetOf(t, isUpdate);
            var whereAt = TopLevelIndex(t, "WHERE");
            var reason = whereAt < 0 ? "sem WHERE" : FiltersNothing(t, whereAt + 1) ? "com WHERE que não filtra nenhuma linha (não referencia colunas ou é sempre verdadeiro)" : null;
            if (reason is null) return null;
            var shown = target ?? "a tabela";
            return D(isUpdate ? DangerKind.UpdateWithoutWhere : DangerKind.DeleteWithoutWhere, target,
                isUpdate ? $"UPDATE em {shown} {reason} vai afetar a tabela inteira" : $"DELETE em {shown} {reason} vai apagar todas as linhas da tabela",
                target is null ? [] : [$"SELECT COUNT(*) FROM {target}"]);
        }

        if (first.Is("TRUNCATE"))
        {
            var target = NameAfter(t, t.Count > 1 && t[1].Is("TABLE") ? 2 : 1);
            return D(DangerKind.TruncateTable, target, $"TRUNCATE TABLE em {target} remove todas as linhas da tabela",
                target is null ? [] : [$"SELECT COUNT(*) FROM {target}"]);
        }

        if (first.Is("DROP"))
        {
            var what = t.Count > 1 ? t[1].Text.ToUpperInvariant() : "objeto";
            var nameAt = t.Count > 2 && t[2].Is("IF") ? 5 : 2;
            var target = NameAfter(t, nameAt);
            var counts = what == "TABLE" && target is not null ? new[] { $"SELECT COUNT(*) FROM {target}" } : [];
            return D(DangerKind.Drop, target, $"DROP {what} {target} apaga o objeto de forma definitiva", counts);
        }

        if (first.Is("ALTER"))
        {
            var isTable = t.Count > 1 && t[1].Is("TABLE");
            var target = isTable ? NameAfter(t, 2) : null;
            var drops = isTable && t.Select((x, i) => (x, i)).Any(p => p.x.Is("DROP") && p.i + 1 < t.Count && t[p.i + 1].Is("COLUMN"));
            return drops
                ? D(DangerKind.DropColumn, target, $"ALTER TABLE em {target} remove coluna(s) e os dados delas")
                : D(DangerKind.AlterTable, target, $"ALTER {(isTable ? "TABLE " + target : t.Count > 1 ? t[1].Text.ToUpperInvariant() : "")} muda a estrutura do banco");
        }
        return null;
    }

    /// <summary>Índice da palavra no nível 0 de parênteses, ou -1.</summary>
    private static int TopLevelIndex(IReadOnlyList<Token> t, string word)
    {
        var depth = 0;
        for (var i = 0; i < t.Count; i++)
        {
            if (t[i].Kind == TokenKind.Symbol && t[i].Text == "(") depth++;
            else if (t[i].Kind == TokenKind.Symbol && t[i].Text == ")") depth--;
            else if (depth == 0 && t[i].Is(word)) return i;
        }
        return -1;
    }

    /// <summary>O predicado depois do WHERE não filtra: sem identificadores/colunas (só literais e operadores) ou tautologia conhecida.</summary>
    private static bool FiltersNothing(IReadOnlyList<Token> t, int from)
    {
        var end = t.Count;
        for (var i = from; i < t.Count; i++)
            if (t[i].Is("ORDER") || t[i].Is("LIMIT")) { end = i; break; }
        var pred = t.Skip(from).Take(end - from).ToList();
        if (pred.Count == 0) return true;
        var hasColumn = pred.Any(x => (x.Kind == TokenKind.Word && !IsLiteralWord(x.Text)) || (x.Kind == TokenKind.Quoted && x.Text[0] == '`'));
        return !hasColumn;
    }

    private static bool IsLiteralWord(string w) =>
        double.TryParse(w, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _) ||
        w.Equals("TRUE", StringComparison.OrdinalIgnoreCase) || w.Equals("FALSE", StringComparison.OrdinalIgnoreCase) ||
        w.Equals("NULL", StringComparison.OrdinalIgnoreCase) || w.Equals("AND", StringComparison.OrdinalIgnoreCase) ||
        w.Equals("OR", StringComparison.OrdinalIgnoreCase) || w.Equals("NOT", StringComparison.OrdinalIgnoreCase) ||
        w.Equals("IS", StringComparison.OrdinalIgnoreCase);

    /// <summary>Alvo de UPDATE (palavra logo após UPDATE, pulando LOW_PRIORITY/IGNORE) ou DELETE (após FROM).</summary>
    private static string? TargetOf(IReadOnlyList<Token> t, bool isUpdate)
    {
        var i = 1;
        if (isUpdate)
        {
            while (i < t.Count && (t[i].Is("LOW_PRIORITY") || t[i].Is("IGNORE"))) i++;
            return NameAfter(t, i);
        }
        var from = t.ToList().FindIndex(x => x.Is("FROM"));
        return from < 0 ? null : NameAfter(t, from + 1);
    }

    /// <summary>Nome possivelmente qualificado (<c>db.tabela</c>, com crases) a partir de <paramref name="index"/>.</summary>
    private static string? NameAfter(IReadOnlyList<Token> t, int index)
    {
        if (index >= t.Count || t[index].Kind is not (TokenKind.Word or TokenKind.Quoted)) return null;
        var name = t[index].Text;
        var i = index + 1;
        while (i + 1 < t.Count && t[i].Kind == TokenKind.Symbol && t[i].Text == "." && t[i + 1].Kind is TokenKind.Word or TokenKind.Quoted)
        {
            name += "." + t[i + 1].Text;
            i += 2;
        }
        return name;
    }

    private static bool LineIsBlank(string text, int cursor)
    {
        var s = cursor; while (s > 0 && text[s - 1] != '\n' && text[s - 1] != '\r') s--;
        var e = cursor; while (e < text.Length && text[e] != '\n' && text[e] != '\r') e++;
        return text.AsSpan(s, e - s).IsWhiteSpace();
    }
}
```

Em `Models.cs`, adicionar `AlterTable,` ao enum `DangerKind` (antes de `Unanalyzable`).

- [ ] **Step 5: Rodar os testes novos; ajustar até passarem**

Run: `dotnet test tests/SqlDesk.SqlAnalysis.Tests --filter MySqlAnalyzerTests --nologo -v q`
Expected: todos passam. Se um falhar, corrigir o analisador (não o teste): cada caso do teste é uma regra da seção "Regras de perigo".

- [ ] **Step 6: Suíte toda** (`dotnet test SqlDesk.sln --nologo -v q`) e commit

```bash
git add src/SqlDesk.SqlAnalysis tests/SqlDesk.SqlAnalysis.Tests
git commit -m "Adiciona o analisador léxico de MySQL com detecção de comandos destrutivos"
```

---

### Task 5: `IDatabaseProvider`, `SqlServerProvider` e o registro

**Files:**
- Create: `src/SqlDesk.Core/Providers/IDatabaseProvider.cs`
- Create: `src/SqlDesk.Core/Providers/SqlServerProvider.cs`
- Create: `src/SqlDesk.Core/Providers/ProviderRegistry.cs`
- Test: `tests/SqlDesk.Core.Tests/ProviderTests.cs`

**Interfaces:**
- Produces:
```csharp
public sealed record ConnectionFailure(string Message, bool CertificateUntrusted);

public interface IDatabaseProvider
{
    string Id { get; }
    string DisplayName { get; }
    int DefaultPort { get; }
    ISqlAnalyzer Analyzer { get; }
    /// <summary>true: DDL (DROP/TRUNCATE/ALTER) confirma a transação sozinho e não dá para desfazer.</summary>
    bool DdlCommitsImplicitly { get; }

    string BuildConnectionString(ConnectionSettings settings, string? password);
    (ConnectionSettings Settings, string? Password) ParseConnectionString(string connectionString);
    DbConnection CreateConnection(string connectionString);
    ConnectionFailure Translate(DbException ex);

    /// <summary>Recebe mensagens informativas do servidor (PRINT / avisos). Dispose cancela a inscrição.</summary>
    IDisposable SubscribeInfoMessages(DbConnection connection, Action<string> onMessage);
    /// <summary>
    /// Liga o aviso "N linhas afetadas" por statement. Devolve false se o driver não tem esse evento
    /// (o QueryRunner então usa <c>DbDataReader.RecordsAffected</c>).
    /// </summary>
    bool TryAttachStatementCompleted(DbCommand command, Action<long> onCompleted);
    /// <summary>Número do erro do servidor (para o erro 334 etc.), ou null.</summary>
    int? ErrorNumber(DbException ex);
    IEnumerable<(string Message, int? Line, bool IsError)> ErrorDetails(DbException ex, int batchFirstLine);

    ITransactionSql Transactions { get; }
    IMetadataSql Metadata { get; }
}

public interface ITransactionSql
{
    /// <summary>Consulta que devolve quantas transações estão abertas (0 = nenhuma). null = o provedor não consegue consultar (ver <see cref="ITransactionProbe"/>).</summary>
    string? OpenCountSql { get; }
    string BeginSql { get; }
    string CommitAllSql { get; }
    string RollbackAllSql { get; }
    string SavepointSql(string name);
    string RollbackToSavepointSql(string name);
    string CountRowsSql(string quotedTable);
}

public interface IMetadataSql
{
    string ObjectsSql { get; }
    string ColumnsSql { get; }
    /// <summary>Converte o tipo de objeto devolvido pela consulta para "table"|"view"|"procedure"|"function".</summary>
    string ObjectKind(string rawType);
    /// <summary>Lê uma linha da consulta de colunas para o tipo no formato de exibição.</summary>
    string FormatType(System.Data.Common.DbDataReader row);
    bool HasSchemaLevel { get; }          // SQL Server: true; MySQL: false (banco == schema)
}
```
- `ProviderRegistry.Get(string id)` (lança `ConnectionValidationException` se desconhecido), `ProviderRegistry.For(ConnectionSettings s)`, `ProviderRegistry.All`.

- [ ] **Step 1: Teste que falha**

```csharp
// tests/SqlDesk.Core.Tests/ProviderTests.cs
using SqlDesk.Core.Connections;
using SqlDesk.Core.Providers;

namespace SqlDesk.Core.Tests;

public class ProviderTests
{
    [Fact]
    public void Registro_resolve_pelo_id_e_pela_conexao()
    {
        Assert.Equal(ProviderIds.SqlServer, ProviderRegistry.Get(ProviderIds.SqlServer).Id);
        Assert.Equal(ProviderIds.SqlServer, ProviderRegistry.For(new ConnectionSettings("s", "d", "u")).Id);
        Assert.Throws<ConnectionValidationException>(() => ProviderRegistry.Get("oracle"));
    }

    [Fact]
    public void SqlServer_expoe_o_sql_de_transacao_atual()
    {
        var p = ProviderRegistry.Get(ProviderIds.SqlServer);
        Assert.Equal("SELECT @@TRANCOUNT", p.Transactions.OpenCountSql);
        Assert.Equal("BEGIN TRANSACTION", p.Transactions.BeginSql);
        Assert.Equal("WHILE @@TRANCOUNT > 0 COMMIT TRANSACTION", p.Transactions.CommitAllSql);
        Assert.Equal("IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION", p.Transactions.RollbackAllSql);
        Assert.Equal("SAVE TRANSACTION sp1", p.Transactions.SavepointSql("sp1"));
        Assert.Equal("ROLLBACK TRANSACTION sp1", p.Transactions.RollbackToSavepointSql("sp1"));
        Assert.False(p.DdlCommitsImplicitly);
    }

    [Fact]
    public void SqlServer_traduz_login_invalido()
    {
        var p = ProviderRegistry.Get(ProviderIds.SqlServer);
        var settings = new ConnectionSettings("127.0.0.1,1", "d", "u", ConnectTimeout: 2);
        Assert.Equal(ProviderIds.SqlServer, p.Id);
        Assert.Contains("1433", p.DefaultPort.ToString());
        Assert.NotNull(p.BuildConnectionString(settings, "x"));
    }
}
```

- [ ] **Step 2: Rodar e ver falhar** (erro de compilação).

- [ ] **Step 3: Implementar** as interfaces como acima e o `SqlServerProvider`:

- `Id = ProviderIds.SqlServer`, `DisplayName = "SQL Server"`, `DefaultPort = 1433`, `Analyzer = SqlServerAnalyzer.Instance`, `DdlCommitsImplicitly = false`.
- `BuildConnectionString`/`ParseConnectionString` delegam a `ConnectionStringService.Build/Parse` (código atual, sem mudança; o `Parse` devolve `Settings` com `Provider = SqlServer` por padrão).
- `CreateConnection(cs) => new SqlConnection(cs)`.
- `Translate(DbException ex)`: `ex is SqlException s ? new ConnectionFailure(SqlErrorTranslator.Translate(s), SqlErrorTranslator.IsCertificateError(s)) : new ConnectionFailure(ex.Message, false)`.
- `SubscribeInfoMessages`: `((SqlConnection)c).InfoMessage += handler` com handler que chama `onMessage(err.Message)` para cada `SqlError`; devolve um `IDisposable` que remove o handler (use uma classe interna `Unsubscribe(Action)`).
- `TryAttachStatementCompleted(cmd, cb)`: `if (cmd is not SqlCommand sc) return false; sc.StatementCompleted += (_, e) => { if (e.RecordCount >= 0) cb(e.RecordCount); }; return true;`
- `ErrorNumber(ex) => (ex as SqlException)?.Number`.
- `ErrorDetails`: para cada `SqlError err` em `((SqlException)ex).Errors`: `(err.Message, string.IsNullOrEmpty(err.Procedure) && err.LineNumber > 0 ? batchFirstLine + err.LineNumber - 1 : null, err.Class > 10 || count == 1)` (idêntico ao `catch (SqlException)` atual do `QueryRunner`).
- `Transactions`: strings exatamente como as do teste acima; `CountRowsSql(t) => $"SELECT COUNT_BIG(*) FROM {t}"`.
- `Metadata`: `ObjectsSql`/`ColumnsSql` = `MetadataQueries.Objects`/`MetadataQueries.Columns` (código atual); `ObjectKind(raw) => MetadataReader.ObjectKind(raw)`; `FormatType(row) => MetadataReader.FormatType(row.GetString(3), Convert.ToInt32(row.GetValue(4)), Convert.ToInt32(row.GetValue(5)), Convert.ToInt32(row.GetValue(6)))`; `HasSchemaLevel = true`.

`ProviderRegistry`: dicionário estático com `SqlServerProvider` (o `MySqlProvider` entra na Task 7); `Get` lança `ConnectionValidationException($"Tipo de servidor desconhecido: {id}.")`.

- [ ] **Step 4: Suíte toda passa.** Run: `dotnet test SqlDesk.sln --nologo -v q`

- [ ] **Step 5: Commit**

```bash
git add src/SqlDesk.Core tests/SqlDesk.Core.Tests
git commit -m "Adiciona a interface de provedor de banco e o provedor do SQL Server"
```

---

### Task 6: Core e Host passam a usar `DbConnection` e o provedor da aba

**Files:**
- Modify: `src/SqlDesk.Core/Sessions/TabSessionManager.cs`
- Modify: `src/SqlDesk.Core/Execution/QueryRunner.cs`
- Modify: `src/SqlDesk.Core/Execution/TransactionService.cs` (inclui `SqlSessionDb`)
- Modify: `src/SqlDesk.Core/Execution/ExecutionModels.cs` (`ISessionDb`)
- Modify: `src/SqlDesk.Core/Execution/GuardedRunner.cs`
- Modify: `src/SqlDesk.Core/Metadata/MetadataService.cs`
- Modify: `src/SqlDesk.Core/Connections/ConnectionTester.cs`
- Modify: `src/SqlDesk.Host/Handlers/{ExecutionHandlers,TransactionHandlers,ConnectionHandlers,ExportHandlers}.cs`, `Bridge/Contracts.cs`, `App.xaml.cs`
- Test: `tests/SqlDesk.Core.Tests/GuardedRunnerTests.cs` e demais que constroem esses tipos

**Interfaces:**
- Consumes: `IDatabaseProvider`, `ProviderRegistry` (Task 5).
- Produces:
  - `TabSessionManager.GetConnection(tabId) : DbConnection?`, `GetProvider(string tabId) : IDatabaseProvider?`, `OpenSideConnectionAsync(Guid, CT) : Task<(DbConnection Connection, IDatabaseProvider Provider)>`.
  - `ISessionDb` ganha `IDatabaseProvider ProviderOf(string tabId);` (substitui a necessidade de strings fixas no `GuardedRunner`/`TransactionService`). `TranCountAsync` passa a usar `ProviderOf(tabId).Transactions.OpenCountSql`.
  - `MetadataService(Func<Guid, CancellationToken, Task<(DbConnection, IDatabaseProvider)>>)`.
  - `ParseConnectionStringRequest` ganha `string? Provider`.

- [ ] **Step 1: Adaptar os fakes de teste ao novo contrato (teste falha de compilação)**

No `FakeDb` de `GuardedRunnerTests.cs` adicionar:

```csharp
        public IDatabaseProvider ProviderOf(string tabId) => ProviderRegistry.Get(ProviderIds.SqlServer);
```

(usings: `SqlDesk.Core.Providers`). Fazer o mesmo em qualquer outra implementação de `ISessionDb` nos testes (`grep -rn ": ISessionDb" tests`). Em `MetadataServiceTests.cs`, o construtor com `openConnection` não é usado pelos testes (usam o `loader`); confirmar com `grep`.

- [ ] **Step 2: Rodar e ver falhar** (`ISessionDb` não tem `ProviderOf`).

- [ ] **Step 3: Implementar a refatoração (comportamento SQL Server idêntico)**

1. `TabSessionManager`: `Session(Guid ConnectionId, DbConnection Connection, int CommandTimeout, IDatabaseProvider Provider)`. Em `OpenAsync`/`OpenSideConnectionAsync`: `var provider = ProviderRegistry.For(info.Settings); var conn = provider.CreateConnection(provider.BuildConnectionString(info.Settings, effective));` e trocar `catch (SqlException ex)` por `catch (DbException ex) { var f = provider.Translate(ex); throw new ConnectFailedException(f.Message, ex, f.CertificateUntrusted); }`. `OpenSessionResult(conn.ServerVersion, conn.Database)` continua. Adicionar `GetProvider`.
2. `ConnectionTester.TestAsync`: mesma troca (`ProviderRegistry.For(settings)`, `CreateConnection`, `catch (DbException)`).
3. `QueryRunner.RunAsync`: obter `var provider = sessions.GetProvider(tabId)!;`. Substituir o bloco `OnInfo`/`InfoMessage` por `using var infoSub = provider.SubscribeInfoMessages(conn, msg => sink.Message(MessageKinds.Info, msg, null));`. No comando, trocar `cmd.StatementCompleted += ...` por:

```csharp
                    void Completed(long n)
                    {
                        sink.Message(MessageKinds.Rows, $"({n} {(n == 1 ? "linha afetada" : "linhas afetadas")})", null);
                        sink.StatementCompleted(n);
                    }
                    var driverReports = provider.TryAttachStatementCompleted(cmd, Completed);
                    var lastAffected = 0L;
```

   No laço de result sets, depois do `StreamCurrentAsync`:

```csharp
                        if (!driverReports)
                        {
                            // Sem evento do driver (MySQL): result set devolve count linhas; comando sem result set usa RecordsAffected (acumulado no batch).
                            var affected = emitted ? count : Math.Max(0, reader.RecordsAffected - lastAffected);
                            if (!emitted) lastAffected = Math.Max(lastAffected, reader.RecordsAffected);
                            Completed(affected);
                        }
```

   Trocar `catch (Exception ex) when (cts.IsCancellationRequested && ex is OperationCanceledException or SqlException or InvalidOperationException)` por `... or DbException or InvalidOperationException` e `catch (SqlException ex)` por:

```csharp
                catch (DbException ex)
                {
                    status = RunStatus.Error;
                    errorNumber = provider.ErrorNumber(ex);
                    foreach (var (message, line, isError) in provider.ErrorDetails(ex, batchFirstLine))
                        sink.Message(isError ? MessageKinds.Error : MessageKinds.Info, message, line);
                    break;
                }
```

   (remover `using Microsoft.Data.SqlClient;`, adicionar `System.Data.Common` e `SqlDesk.Core.Providers`).
4. `ISessionDb`: adicionar `IDatabaseProvider ProviderOf(string tabId);`. `SqlSessionDb` (renomear classe para `SessionDb` e arquivo permanece) implementa `ProviderOf => sessions.GetProvider(tabId) ?? throw new TabNotConnectedException(...)`; `Conn` devolve `DbConnection`; `TranCountAsync` usa `ProviderOf(tabId).Transactions.OpenCountSql` (se `null`, lança `NotSupportedException`: a Task 8 trata esse caso antes).
5. `TransactionService`: `BeginAsync/CommitAsync/RollbackAsync` usam `db.ProviderOf(tabId).Transactions.BeginSql/CommitAllSql/RollbackAllSql`.
6. `GuardedRunner`: `OpenScopeAsync` usa `db.ProviderOf(tabId).Transactions.BeginSql` e `.SavepointSql(savepoint)`; `RollbackScopeAsync` usa `RollbackAllSql`/`RollbackToSavepointSql`; `ResolveAsync` usa `CommitAllSql`; `OutputRewriter.Rewrite(plan.Text)` vira `db.ProviderOf(tabId).Analyzer.Rewrite(plan.Text)`; a constante `OutputWithTriggerError` continua (só ocorre no SQL Server).
7. `MetadataService`: ver Task 10 (este task só troca o tipo do delegate para `Task<(DbConnection, IDatabaseProvider)>` mantendo o SQL Server; as consultas vêm de `provider.Metadata`).
8. Host: `ExecuteHandler` recebe `TabSessionManager sessions` e usa `sessions.GetProvider(r.TabId)?.Analyzer ?? throw new BridgeException("not_connected", "A aba não está conectada. Conecte antes de executar.")` em `ExecutionPlanner.Plan(analyzer, ...)`. `ExportHandlers` idem para o `ExportByRerunAsync`. `TranCommandHandler`: `catch (System.Data.Common.DbException ex)`. `ConnectionHandlers`: `BuildConnectionStringHandler` usa `ProviderRegistry.For(r.Settings).BuildConnectionString(...)`; `ParseConnectionStringHandler` usa `ProviderRegistry.Get(r.Provider ?? ProviderIds.SqlServer).ParseConnectionString(...)` e, no retorno, `settings with { Provider = provider.Id }`. `Contracts.cs`: `ParseConnectionStringRequest(string ConnectionString, string? Provider = null)`. `App.xaml.cs`: `ISessionDb, SessionDb` e o factory do `MetadataService` passa a devolver a tupla.

- [ ] **Step 4: Suíte toda (.NET + vitest) passa; build do Host**

Run: `dotnet build SqlDesk.sln --nologo -v q && dotnet test SqlDesk.sln --nologo -v q`
Expected: build sem erros; todos os testes passam. Se algum teste falhar, o comportamento do SQL Server mudou: corrigir a refatoração, não o teste.

- [ ] **Step 5: Commit**

```bash
git add -A src tests
git commit -m "Faz o Core e o Host usarem DbConnection e o provedor da conexão da aba"
```

---

### Task 7: `MySqlProvider` (conexão, connection string, erros)

**Files:**
- Modify: `src/SqlDesk.Core/SqlDesk.Core.csproj` (pacote `MySqlConnector`)
- Create: `src/SqlDesk.Core/Providers/MySqlProvider.cs`
- Modify: `src/SqlDesk.Core/Providers/ProviderRegistry.cs`
- Test: `tests/SqlDesk.Core.Tests/MySqlProviderTests.cs`

**Interfaces:**
- Consumes: Task 5.
- Produces: `MySqlProvider` com `Id = "mysql"`, `DefaultPort = 3306`, `DdlCommitsImplicitly = true`, `Analyzer = MySqlAnalyzer.Instance`.

**Mapeamento de segurança de transporte** (mantém o contrato `encrypt`/`trustServerCertificate`):
`encrypt=false` → `SslMode=None`; `encrypt=true, trustServerCertificate=true` → `SslMode=Required`; `encrypt=true, trustServerCertificate=false` → `SslMode=VerifyCA`. `Parse` faz o inverso (`Preferred` vira `encrypt=true, trust=true`; `VerifyFull`/`VerifyCA` → `trust=false`; `None`/`Disabled` → `encrypt=false`).

- [ ] **Step 1: Testes que falham**

```csharp
// tests/SqlDesk.Core.Tests/MySqlProviderTests.cs
using SqlDesk.Core.Connections;
using SqlDesk.Core.Providers;

namespace SqlDesk.Core.Tests;

public class MySqlProviderTests
{
    private static readonly IDatabaseProvider P = ProviderRegistry.Get(ProviderIds.MySql);

    [Fact]
    public void Build_inclui_porta_e_ssl()
    {
        var cs = P.BuildConnectionString(
            new ConnectionSettings("db.exemplo.com:3307", "vendas", "app", 10, 45, Encrypt: true, TrustServerCertificate: true)
            { Provider = ProviderIds.MySql }, "se;nh=a'\"");
        var b = new MySqlConnector.MySqlConnectionStringBuilder(cs);
        Assert.Equal("db.exemplo.com", b.Server);
        Assert.Equal(3307u, b.Port);
        Assert.Equal("vendas", b.Database);
        Assert.Equal("app", b.UserID);
        Assert.Equal("se;nh=a'\"", b.Password);
        Assert.Equal(MySqlConnector.MySqlSslMode.Required, b.SslMode);
        Assert.Equal(10u, b.ConnectionTimeout);
    }

    [Theory]
    [InlineData(false, false, "None")]
    [InlineData(true, true, "Required")]
    [InlineData(true, false, "VerifyCA")]
    public void Mapeia_encrypt_e_trust_para_ssl(bool encrypt, bool trust, string mode)
    {
        var cs = P.BuildConnectionString(new ConnectionSettings("h", "d", "u", Encrypt: encrypt, TrustServerCertificate: trust) { Provider = ProviderIds.MySql }, null);
        Assert.Equal(mode, new MySqlConnector.MySqlConnectionStringBuilder(cs).SslMode.ToString());
    }

    [Fact]
    public void Parse_e_build_fazem_ida_e_volta()
    {
        var (s, pwd) = P.ParseConnectionString("Server=h;Port=3307;Database=d;User ID=u;Password=p;SslMode=VerifyFull;Connection Timeout=7;Default Command Timeout=50;Application Name=Foo");
        Assert.Equal("h:3307", s.Server);
        Assert.Equal(ProviderIds.MySql, s.Provider);
        Assert.True(s.Encrypt);
        Assert.False(s.TrustServerCertificate);
        Assert.Equal(7, s.ConnectTimeout);
        Assert.Equal(50, s.CommandTimeout);
        Assert.Equal("p", pwd);
        Assert.Equal("Foo", s.Advanced["ApplicationName"]);
    }

    [Fact]
    public void Parse_recusa_chave_desconhecida() =>
        Assert.Throws<ConnectionValidationException>(() => P.ParseConnectionString("Server=h;ChaveInexistente=1"));

    [Fact]
    public void Traduz_erros_comuns()
    {
        var denied = new MySqlConnector.MySqlException(MySqlConnector.MySqlErrorCode.AccessDenied, null, "Access denied for user 'u'@'h'");
        Assert.Contains("usuário ou senha", P.Translate(denied).Message);
        var unknownDb = new MySqlConnector.MySqlException(MySqlConnector.MySqlErrorCode.UnknownDatabase, null, "Unknown database 'x'");
        Assert.Contains("não existe", P.Translate(unknownDb).Message);
    }

    [Fact]
    public void Sql_de_transacao_do_mysql()
    {
        var t = P.Transactions;
        Assert.Equal("START TRANSACTION", t.BeginSql);
        Assert.Equal("COMMIT", t.CommitAllSql);
        Assert.Equal("ROLLBACK", t.RollbackAllSql);
        Assert.Equal("SAVEPOINT sp1", t.SavepointSql("sp1"));
        Assert.Equal("ROLLBACK TO SAVEPOINT sp1", t.RollbackToSavepointSql("sp1"));
        Assert.True(P.DdlCommitsImplicitly);
    }
}
```

Os construtores de `MySqlException` variam entre versões do MySqlConnector: confirmar a assinatura com `dotnet build` e ajustar só a construção do exception no teste `Traduz_erros_comuns` (por exemplo via `MySqlException(MySqlErrorCode, string? sqlState, string message)`).

- [ ] **Step 2: Rodar e ver falhar** (pacote ausente / tipo inexistente).

- [ ] **Step 3: Implementar**

`dotnet add src/SqlDesk.Core package MySqlConnector` e também em `tests/SqlDesk.Core.Tests` (o teste usa os tipos). Implementar `MySqlProvider : IDatabaseProvider`:

- `BuildConnectionString`: `var b = new MySqlConnectionStringBuilder { Server = host, Port = port, Database, UserID, ConnectionTimeout = (uint)ConnectTimeout, DefaultCommandTimeout = (uint)CommandTimeout, SslMode = ..., AllowUserVariables = true, CharacterSet = "utf8mb4" }`; `host`/`port` vêm de `Server` no formato `host` ou `host:porta` (`lastIndexOf(':')` com `ushort.TryParse`; sem porta → 3306); senha só se não vazia; depois `foreach (var (k, v) in s.Advanced) b[k] = v;` (chave inválida → `ConnectionValidationException($"Palavra-chave não suportada: '{k}'.")` capturando `ArgumentException`).
- `ParseConnectionString`: `new MySqlConnectionStringBuilder(cs)` (capturar `ArgumentException` → `ConnectionValidationException("Connection string inválida: ...")`); `Server` = host (mais `:porta` se `Port != 3306`); mapear SslMode como descrito; `Advanced` = demais chaves **que o usuário informou** (comparar com a connection string bruta: iterar `new DbConnectionStringBuilder{ConnectionString = cs}.Keys`, pular as já tratadas, e guardar `b[key]` pelo nome canônico `typeof(MySqlConnectionStringBuilder)`; para obter a chave canônica use `new MySqlConnectionStringBuilder { [key] = value }.ConnectionString.Split('=')[0]` como o `CanonicalKey` do SQL Server). O teste espera `Advanced["ApplicationName"]`; ajustar o teste se a chave canônica for outra, mantendo o contrato "ida e volta preserva".
- `CreateConnection(cs) => new MySqlConnection(cs)`.
- `Translate(DbException ex)`: se `ex is MySqlException m`: `AccessDenied` → "Falha de login: usuário ou senha inválidos."; `UnknownDatabase` → "O banco de dados informado não existe ou o usuário não tem acesso a ele."; `UnableToConnectToHost` ou `m.InnerException is SocketException` → "Não foi possível alcançar o servidor (recusou a conexão, não respondeu ou o nome não existe). Verifique o nome, a porta e a rede."; mensagem com "SSL"/"certificate"/"TLS" (regex, sem diferenciar caixa) → `CertificateUntrusted = true` com o texto "O certificado do servidor não é confiável... Se o servidor é confiável, marque 'Confiar no certificado' (ou desmarque 'Criptografar a conexão'). Detalhe: ..."; demais → `m.Message`.
- `SubscribeInfoMessages`: `((MySqlConnection)c).InfoMessage += (_, e) => { foreach (var err in e.Errors) onMessage(err.Message); }`.
- `TryAttachStatementCompleted => false`.
- `ErrorNumber(ex) => (ex as MySqlException) is { } m ? (int)m.ErrorCode : null`.
- `ErrorDetails`: um item `(ex.Message, null, true)`; MySQL não informa a linha do erro de forma estruturada; se a mensagem contiver `at line N` (regex `at line (\d+)`), `Line = batchFirstLine + N - 1`.
- `Transactions`: strings como no teste; `OpenCountSql = null` (resolvido na Task 8); `CountRowsSql(t) => $"SELECT COUNT(*) FROM {t}"`.
- `Metadata`: implementado na Task 10 (por ora lançar `NotImplementedException` só neste task, **removido na Task 10**; os testes desta task não tocam nele).

Registrar no `ProviderRegistry`.

- [ ] **Step 4: Rodar os testes novos e a suíte.** Expected: passam.

- [ ] **Step 5: Commit**

```bash
git add -A src tests
git commit -m "Adiciona o provedor MySQL com conexão, connection string e tradução de erros"
```

---

### Task 8: Contagem de transação aberta no MySQL e MariaDB

**Files:**
- Create: `tests/SqlDesk.Integration.Tests/SqlDesk.Integration.Tests.csproj`
- Create: `tests/SqlDesk.Integration.Tests/TestServers.cs`
- Create: `tests/SqlDesk.Integration.Tests/TransactionProbeTests.cs`
- Modify: `SqlDesk.sln` (incluir o projeto)
- Create: `src/SqlDesk.Core/Providers/MySqlTransactionProbe.cs`
- Modify: `src/SqlDesk.Core/Execution/TransactionService.cs` (`SessionDb.TranCountAsync`)

**Interfaces:**
- Produces:
  - `TestServers`: `static IEnumerable<object[]> All` (`"mysql"`, `"mariadb"`), `static ConnectionSettings For(string name)`, `static string? Password` (lê `SQLDESK_TEST_PWD`; os testes de integração fazem `Skip` quando a variável não existe, via `Assert.Skip`-equivalente: classe base `IntegrationFactAttribute : FactAttribute` que define `Skip` se faltar a variável ou a porta estiver fechada).
  - `MySqlTransactionProbe.OpenCountAsync(DbConnection conn, CancellationToken ct) : Task<int?>`: tenta, nesta ordem, `SELECT @@in_transaction` (MariaDB), depois `SELECT COUNT(*) FROM performance_schema.events_transactions_current WHERE THREAD_ID = PS_CURRENT_THREAD_ID() AND STATE = 'ACTIVE'` (MySQL 8); devolve `null` se as duas falharem por permissão. O resultado da primeira consulta que funcionar é memorizado por conexão (`ConditionalWeakTable<DbConnection, StrategyBox>`).

- [ ] **Step 1: Projeto de integração e teste que falha**

`SqlDesk.Integration.Tests.csproj`: net8.0, mesmas referências de xunit do `SqlDesk.Core.Tests`, `ProjectReference` para `SqlDesk.Core` e `SqlDesk.SqlAnalysis`. `TestServers.For("mysql")` = `new ConnectionSettings("127.0.0.1:33306", "sqldesk_test", "sqldesk", Encrypt: false) { Provider = "mysql" }`; `"mariadb"` usa a porta `33307`.

```csharp
// tests/SqlDesk.Integration.Tests/TransactionProbeTests.cs
public class TransactionProbeTests
{
    [IntegrationTheory, MemberData(nameof(TestServers.All), MemberType = typeof(TestServers))]
    public async Task Conta_transacao_aberta_e_fechada(string server)
    {
        var provider = ProviderRegistry.Get(ProviderIds.MySql);
        await using var conn = provider.CreateConnection(provider.BuildConnectionString(TestServers.For(server), TestServers.Password));
        await conn.OpenAsync();

        Assert.Equal(0, await MySqlTransactionProbe.OpenCountAsync(conn, default));
        await Exec(conn, "START TRANSACTION");
        Assert.Equal(1, await MySqlTransactionProbe.OpenCountAsync(conn, default));
        await Exec(conn, "ROLLBACK");
        Assert.Equal(0, await MySqlTransactionProbe.OpenCountAsync(conn, default));
    }

    private static async Task Exec(System.Data.Common.DbConnection c, string sql)
    {
        await using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }
}
```

(`START TRANSACTION` sozinho não registra transação "ativa" no InnoDB até a primeira instrução; o `performance_schema` mostra `ACTIVE` assim que `START TRANSACTION` é executado. Se o teste mostrar o contrário num dos servidores, **o resultado real manda**: ajustar o teste e a estratégia da sonda ao que o container devolver e registrar a diferença no README.)

- [ ] **Step 2: Rodar e ver falhar** (`dotnet test tests/SqlDesk.Integration.Tests --nologo -v q` com os containers de pé e `SQLDESK_TEST_PWD` definida): erro de compilação, depois falha da sonda.

- [ ] **Step 3: Implementar a sonda e usá-la**

`MySqlTransactionProbe` como descrito. Em `SessionDb.TranCountAsync`:

```csharp
    public async Task<int> TranCountAsync(string tabId, CancellationToken ct)
    {
        var provider = ProviderOf(tabId);
        if (provider.Transactions.OpenCountSql is { } sql)
        {
            await using var cmd = Conn(tabId).CreateCommand();
            cmd.CommandText = sql;
            return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
        }
        // MySQL/MariaDB: sonda; sem permissão para consultar, assume que há transação aberta se o app abriu uma (estado rastreado).
        return await MySqlTransactionProbe.OpenCountAsync(Conn(tabId), ct) ?? sessions.TrackedTransactionCount(tabId);
    }
```

Adicionar em `TabSessionManager` um contador rastreado por aba (`TrackedTransactionCount`, `SetTrackedTransactionCount`) atualizado por `TransactionService.BeginAsync` (=1), `CommitAsync`/`RollbackAsync` (=0). Uma consulta que não pôde ser feita **nunca** devolve 0 "por falta de informação" a menos que o app mesmo tenha confirmado/desfeito: erra para "há transação" (avisa ao fechar a aba) em vez de "não há".

- [ ] **Step 4: Rodar integração e a suíte.** Expected: passam nos dois servidores.

- [ ] **Step 5: Commit**

```bash
git add -A src tests SqlDesk.sln
git commit -m "Detecta transação aberta no MySQL e no MariaDB e cria o projeto de testes de integração"
```

---

### Task 9: Travas no MySQL (contagem prévia, DDL irreversível)

**Files:**
- Modify: `src/SqlDesk.Core/Execution/GuardedRunner.cs`
- Modify: `src/SqlDesk.Core/Execution/GuardModels.cs` (`GuardOutcome`, `GuardChange`)
- Modify: `src/SqlDesk.Core/Execution/ExecutionPlanner.cs` (`Dangerous.Irreversible`)
- Modify: `src/SqlDesk.Host/Handlers/ExecutionHandlers.cs`, `Bridge/Contracts.cs`
- Test: `tests/SqlDesk.Core.Tests/GuardedRunnerTests.cs`, `tests/SqlDesk.Integration.Tests/GuardTests.cs`

**Interfaces:**
- Consumes: `IDatabaseProvider.DdlCommitsImplicitly`, `Analyzer.Rewrite`, `Transactions`.
- Produces:
  - `ExecutionPlan.Dangerous.Irreversible : bool` = `provider.DdlCommitsImplicitly && Dangers.Any(d => d.Kind is TruncateTable or Drop or DropColumn or AlterTable)`. O planejador não conhece o provedor: o `ExecuteHandler` calcula o valor com `DangerKinds.IsDdl` e passa na resposta.
  - `public static class DangerKinds { public static bool IsDdl(DangerKind k) => k is DangerKind.TruncateTable or DangerKind.Drop or DangerKind.DropColumn or DangerKind.AlterTable; }` em SqlAnalysis.
  - `ExecuteResponse` ganha `bool Irreversible = false` (Contracts.cs) preenchido em `needs_confirmation` quando `provider.DdlCommitsImplicitly && danger.Dangers.Any(d => DangerKinds.IsDdl(d.Kind))`.
  - `GuardStatus.Completed = "completed"`: a execução com perigo irreversível roda **sem transação e sem segunda confirmação**.

- [ ] **Step 1: Testes que falham**

Em `GuardedRunnerTests.cs`, com um `FakeDb` cujo `ProviderOf` devolve o `MySqlProvider` (parametrizar o fake: `public IDatabaseProvider Provider = ProviderRegistry.Get(ProviderIds.SqlServer)`):

```csharp
    [Fact]
    public async Task Mysql_DDL_executa_sem_transacao_e_sem_segunda_confirmacao()
    {
        var db = new FakeDb { Provider = ProviderRegistry.Get(ProviderIds.MySql) };
        var runner = new FakeRunner { Db = db };
        runner.Script.Enqueue((sink, _) => new RunSummary(RunStatus.Completed, 1, 0));
        var guard = new GuardedRunner(db, runner);
        var plan = (ExecutionPlan.Dangerous)ExecutionPlanner.Plan(MySqlAnalyzer.Instance, "DROP TABLE t", 0, 0, 0, wholeScript: true);

        var outcome = await guard.RunAsync("tab", "e1", plan, null, new NullSink(), default);

        Assert.Equal(GuardStatus.Completed, outcome.Status);
        Assert.Null(outcome.Pending);
        Assert.DoesNotContain("START TRANSACTION", db.Commands);
        Assert.False(guard.HasPending("tab"));
    }

    [Fact]
    public async Task Mysql_UPDATE_sem_where_usa_contagem_previa_e_fica_pendente()
    {
        var db = new FakeDb { Provider = ProviderRegistry.Get(ProviderIds.MySql) };
        db.Counts["SELECT COUNT(*) FROM t"] = 42;
        var runner = new FakeRunner { Db = db };
        runner.Script.Enqueue((sink, d) => { sink.StatementCompleted(42); return new RunSummary(RunStatus.Completed, 1, 0); });
        var guard = new GuardedRunner(db, runner);
        var plan = (ExecutionPlan.Dangerous)ExecutionPlanner.Plan(MySqlAnalyzer.Instance, "UPDATE t SET a = 1", 0, 0, 0, wholeScript: true);

        var outcome = await guard.RunAsync("tab", "e1", plan, null, new NullSink(), default);

        Assert.Equal(GuardStatus.PendingDecision, outcome.Status);
        var change = Assert.Single(outcome.Pending!.Changes);
        Assert.Equal(42, change.AffectedRows);
        Assert.False(change.HasPreview);
        Assert.Contains("START TRANSACTION", db.Commands);
    }
```

O `FakeDb.ExecAsync` precisa reconhecer `START TRANSACTION` (incrementa `Count`) e `ROLLBACK`/`COMMIT` (zera). Ajustar o fake com os mesmos `if` do SQL Server.

- [ ] **Step 2: Rodar e ver falhar.**

- [ ] **Step 3: Implementar**

Em `GuardedRunner.RunAsync`, logo depois das checagens iniciais e **antes** de abrir transação:

```csharp
        var provider = db.ProviderOf(tabId);
        if (provider.DdlCommitsImplicitly && plan.Dangers.Any(d => DangerKinds.IsDdl(d.Kind)))
        {
            // DDL faz commit sozinho no MySQL/MariaDB: não há o que desfazer, então não há segunda confirmação.
            sink.Message(MessageKinds.Info, "Este comando muda a estrutura do banco e não pode ser desfeito (o MySQL confirma DDL sozinho).", null);
            var direct = await runner.RunAsync(tabId, plan.Batches, plan.BaseOffset, plan.BaseLine, maxRows, sink, ct);
            return new GuardOutcome(direct.Status == RunStatus.Completed ? GuardStatus.Completed : direct.Status, direct.ElapsedMs, direct.TotalRows, null);
        }
```

Para UPDATE/DELETE sem WHERE, o fluxo existente já serve: `rewrite.Rewritten` vazio → `GuardReportBuilder.Build` cai no ramo `counts` (a contagem prévia vem de `CountQueries`, gerada pelo analisador MySQL). Ajustar `GuardedRunner` para usar `provider.Transactions.CountRowsSql` **não** é necessário: o SQL da contagem já vem pronto no `DangerousStatement`. `GuardStatus.Completed = "completed"` entra em `GuardModels.cs`. `GuardInfo.PreviewUnavailable` fica `false` em MySQL (a amostra nunca existiu); para o frontend não mostrar "sem amostra por trigger", enviar `previewUnavailable: false` e `HasPreview: false` (já é o resultado do builder).

Em `ExecuteHandler`, no caso `needs_confirmation`, calcular `irreversible` com `sessions.GetProvider(r.TabId)!.DdlCommitsImplicitly && danger.Dangers.Any(d => DangerKinds.IsDdl(d.Kind))` e devolver em `ExecuteResponse(..., Irreversible: irreversible)`.

- [ ] **Step 4: Teste de integração de ponta a ponta**

```csharp
// tests/SqlDesk.Integration.Tests/GuardTests.cs
public class GuardTests
{
    [IntegrationTheory, MemberData(nameof(TestServers.All), MemberType = typeof(TestServers))]
    public async Task Delete_sem_where_faz_rollback_de_verdade(string server)
    {
        await using var h = await Harness.OpenAsync(server);          // helper: abre TabSessionManager+QueryRunner+GuardedRunner numa aba
        await h.RunAsync("DROP TABLE IF EXISTS g1; CREATE TABLE g1 (id INT PRIMARY KEY) ENGINE=InnoDB; INSERT INTO g1 VALUES (1),(2),(3);");

        var outcome = await h.RunGuardedAsync("DELETE FROM g1");
        Assert.Equal(GuardStatus.PendingDecision, outcome.Status);
        Assert.Equal(3, outcome.Pending!.Changes.Single().AffectedRows);

        await h.Guard.ResolveAsync(h.TabId, outcome.Pending.GuardId, commit: false);
        Assert.Equal(3L, await h.ScalarAsync("SELECT COUNT(*) FROM g1"));
    }

    [IntegrationTheory, MemberData(nameof(TestServers.All), MemberType = typeof(TestServers))]
    public async Task Drop_executa_sem_segunda_confirmacao(string server)
    {
        await using var h = await Harness.OpenAsync(server);
        await h.RunAsync("DROP TABLE IF EXISTS g2; CREATE TABLE g2 (id INT)");
        var outcome = await h.RunGuardedAsync("DROP TABLE g2");
        Assert.Equal(GuardStatus.Completed, outcome.Status);
        Assert.Null(outcome.Pending);
    }
}
```

Criar `Harness` em `tests/SqlDesk.Integration.Tests/Harness.cs`: monta `ConnectionStore` num diretório temporário (protetor sem criptografia), salva a conexão de `TestServers.For(server)` com a senha, cria `TabSessionManager`, `QueryRunner`, `SessionDb`, `GuardedRunner`; `OpenAsync` abre a aba `"t1"`; `RunAsync(sql)` usa `ExecutionPlanner.Plan(analyzer, ...)` + `QueryRunner.RunAsync` com um sink que ignora tudo; `RunGuardedAsync(sql)` faz o `Plan` esperando `Dangerous` e chama `GuardedRunner.RunAsync`; `ScalarAsync(sql)` roda `SELECT` pela conexão da aba. Implementar `IAsyncDisposable` para desconectar.

- [ ] **Step 5: Rodar suíte e integração.** Expected: passam nos dois servidores.

- [ ] **Step 6: Commit**

```bash
git add -A src tests
git commit -m "Adapta as travas ao MySQL: contagem prévia, rollback real e DDL sem falsa segunda confirmação"
```

---

### Task 10: Metadados e árvore de objetos do MySQL

**Files:**
- Modify: `src/SqlDesk.Core/Providers/MySqlProvider.cs` (`Metadata`)
- Modify: `src/SqlDesk.Core/Metadata/MetadataService.cs`
- Modify: `src/SqlDesk.Host/Bridge/Contracts.cs` (`MetadataDto.HasSchemaLevel`), `Handlers/MetadataHandlers.cs`
- Modify: `src/SqlDesk.Web/src/contracts.ts`, `metadataIndex.ts`, `components/ObjectTree.tsx`, `suggest.ts` (somente o que depender de `dbo`)
- Test: `tests/SqlDesk.Core.Tests/MetadataTests.cs`, `tests/SqlDesk.Integration.Tests/MetadataIntegrationTests.cs`, `src/SqlDesk.Web/src/metadataIndex.test.ts` (novo)

**Interfaces:**
- Produces:
  - `MySqlMetadataSql`:
    - `ObjectsSql`: `SELECT TABLE_SCHEMA, TABLE_NAME, CASE TABLE_TYPE WHEN 'VIEW' THEN 'V' ELSE 'U' END FROM information_schema.TABLES WHERE TABLE_SCHEMA NOT IN ('mysql','information_schema','performance_schema','sys') UNION ALL SELECT ROUTINE_SCHEMA, ROUTINE_NAME, CASE ROUTINE_TYPE WHEN 'PROCEDURE' THEN 'P' ELSE 'FN' END FROM information_schema.ROUTINES WHERE ROUTINE_SCHEMA NOT IN ('mysql','information_schema','performance_schema','sys') ORDER BY 1, 2`
    - `ColumnsSql`: `SELECT TABLE_SCHEMA, TABLE_NAME, COLUMN_NAME, COLUMN_TYPE, 0, 0, 0, CASE IS_NULLABLE WHEN 'YES' THEN 1 ELSE 0 END FROM information_schema.COLUMNS WHERE TABLE_SCHEMA NOT IN (...mesmos...) ORDER BY TABLE_SCHEMA, TABLE_NAME, ORDINAL_POSITION`
    - `ObjectKind`: reaproveita `MetadataReader.ObjectKind` (mesmos códigos `U/V/P/FN`); `FormatType(row) => row.GetString(3)` (`COLUMN_TYPE` já vem como `varchar(50)`, `decimal(10,2)`, `tinyint(1)`); `HasSchemaLevel = false`.
  - `MetadataService` lê `provider.Metadata.ObjectsSql/ColumnsSql`, usa `provider.Metadata.FormatType` e `ObjectKind` (substituir os usos diretos de `MetadataQueries`, `MetadataReader.ObjectKind` e `MetadataReader.FormatType` por chamadas pelo provedor). A chave de colunas continua `schema.objeto`: no MySQL, `schema` = banco.
  - `MetadataDto.HasSchemaLevel` (`bool`), preenchido a partir de `snapshot.HasSchemaLevel` (novo campo em `MetadataSnapshot`).
  - Frontend: `MetadataDto.hasSchemaLevel: boolean`; `MetadataIndex.hasSchemaLevel`. `ObjectTree` com `hasSchemaLevel=false` mostra os bancos como nível raiz (rótulo "banco") e **não** o rótulo "schema". `find(schema, name)` sem schema, quando `hasSchemaLevel` é falso, prefere o banco atual da conexão (`ConnectionInfo.settings.database`) em vez de `dbo`.

- [ ] **Step 1: Testes que falham**

xUnit (`MetadataTests.cs`): `MySqlMetadataSql.FormatType` devolve `decimal(10,2)` quando o reader fake traz `COLUMN_TYPE`; usar o mesmo estilo de fake de `DbDataReader` que `MetadataTests.cs` já usa (abrir o arquivo e seguir o padrão). Integração (`MetadataIntegrationTests.cs`): criar `m1(id INT, nome VARCHAR(50))` e uma view, chamar o `MetadataService` com o loader real, aguardar `MetaPhase.Columns` e afirmar que `Objects` contém `m1` como `table`, a view como `view`, e `Columns["sqldesk_test.m1"]` tem `varchar(50)` e `int`. vitest (`metadataIndex.test.ts`): com `hasSchemaLevel:false` e dois bancos com tabela homônima, `find(undefined, 'x')` prefere o banco informado.

- [ ] **Step 2: Rodar e ver falhar** (.NET: compilação; vitest: `hasSchemaLevel` inexistente).

- [ ] **Step 3: Implementar** o que está em **Interfaces**. No `ObjectTree`, o primeiro nível continua sendo `index.schemas` (os bancos); só muda o texto de rótulos/vazio ("Nenhum objeto visível neste banco." já serve) e o ícone, se houver ícone específico de schema.

- [ ] **Step 4: Suítes** (`dotnet test SqlDesk.sln`, integração com containers, `npm test`, `npm run build`). Expected: tudo passa.

- [ ] **Step 5: Commit**

```bash
git add -A src tests
git commit -m "Carrega metadados do MySQL pelo information_schema e adapta a árvore de objetos"
```

---

### Task 11: Tipos do MySQL na grade e na exportação

**Files:**
- Modify: `src/SqlDesk.Core/Execution/CellValues.cs`
- Modify: `src/SqlDesk.Core/Export/ExportValues.cs` (se `Coerce` falhar com tipos MySqlConnector)
- Test: `tests/SqlDesk.Core.Tests/ExecutionTests.cs` (ou `CellValuesTests.cs` novo), `tests/SqlDesk.Integration.Tests/TypesTests.cs`

**Interfaces:**
- Produces: `CellValues.Convert` aceita `MySqlConnector.MySqlDateTime` (data zero → texto `0000-00-00`), `sbyte`/`ushort`/`uint`/`ulong` (`ulong` → texto, como `long`), `TimeSpan` negativo/maior que 24 h (`MySQL TIME` pode ir de -838:59:59 a 838:59:59: formatar `[-]HHH:mm:ss`), `bool` de `TINYINT(1)` já vem como `bool` com `TreatTinyAsBoolean` (padrão). `KindOf` trata `ulong`/`uint`/`ushort`/`sbyte` como `number`.

- [ ] **Step 1: Testes que falham**

```csharp
public class MySqlCellValueTests
{
    [Fact] public void Ulong_vira_texto() => Assert.Equal("18446744073709551615", CellValues.Convert(ulong.MaxValue));
    [Fact] public void Uint_e_ushort_e_sbyte_viram_numero() { Assert.Equal(5u, CellValues.Convert(5u)); Assert.Equal((ushort)5, CellValues.Convert((ushort)5)); Assert.Equal((sbyte)-5, CellValues.Convert((sbyte)-5)); }
    [Fact] public void Time_longo_do_mysql() => Assert.Equal("838:59:59", CellValues.Convert(new TimeSpan(34, 22, 59, 59)));
    [Fact] public void Time_negativo() => Assert.Equal("-01:30:00", CellValues.Convert(new TimeSpan(-1, -30, 0)));
    [Fact] public void Data_zero_do_mysql() => Assert.Equal("0000-00-00", CellValues.Convert(new MySqlConnector.MySqlDateTime()));
    [Fact] public void Kind_dos_inteiros_sem_sinal() { Assert.Equal("number", CellValues.KindOf(typeof(ulong))); Assert.Equal("number", CellValues.KindOf(typeof(uint))); }
}
```

Integração (`TypesTests`): `SELECT CAST('0000-00-00' AS CHAR)`, não funciona com `AllowZeroDateTime=false`; usar `ConnectionSettings.Advanced["AllowZeroDateTime"]="true"` e `SELECT DATE '0000-00-00'` fica fora (modo SQL estrito rejeita). Em vez disso, no teste de integração: criar tabela com `TINYINT(1)`, `BIT(3)`, `JSON`, `BLOB`, `DECIMAL(30,10)`, `DATETIME(3)`, `TIME(3)`, inserir uma linha e executar `SELECT *` com `CapturingSink`, afirmando: nenhuma exceção, `tinyint(1)` vira bool, `DECIMAL` vira texto, `BLOB` vira `0x...`, `JSON` vira texto.

- [ ] **Step 2: Rodar e ver falhar.**

- [ ] **Step 3: Implementar** os `case` novos em `CellValues.Convert` (antes do `default`) e em `KindOf`. Para `TimeSpan`, trocar o formato atual por uma função `FormatTime(ts)` que escreve `[-]` + `(int)Math.Abs(ts.TotalHours)` com no mínimo 2 dígitos + `mm:ss` + fração (`.FFFFFFF` aparado). Não usar `MySqlConnector` no `CellValues` por tipo estático: usar `value.GetType().Name == "MySqlDateTime"` para o Core não depender do pacote nessa classe, **ou** referenciar o pacote (já referenciado pelo Core desde a Task 7; preferir o tipo estático).

- [ ] **Step 4: Verificar o `ExportValues.Coerce`** com a mesma tabela do teste de integração: exportar CSV e XLSX (`ExportService.ExportLoadedAsync`/`ExportByRerunAsync`) para um arquivo temporário e conferir que o arquivo existe e tem a linha. Corrigir `Coerce` somente se falhar.

- [ ] **Step 5: Suítes e commit**

```bash
git add -A src tests
git commit -m "Converte os tipos do MySQL na grade e na exportação"
```

---

### Task 12: Diálogo de conexão, lista e dialogs de trava no frontend

**Files:**
- Modify: `src/SqlDesk.Web/src/contracts.ts` (`ConnectionSettings.provider`, `ExecuteResponse.irreversible`, `DangerKind` com `alterTable`, `GuardStatus 'completed'`)
- Modify: `src/SqlDesk.Web/src/components/ConnectionDialog.tsx`
- Modify: `src/SqlDesk.Web/src/components/Sidebar.tsx`, `StatusBar.tsx` (rótulo/ícone do provedor)
- Modify: `src/SqlDesk.Web/src/components/GuardDialogs.tsx`, `guardDiff.ts`, `App.tsx`
- Create: `src/SqlDesk.Web/src/providers.ts` (+ `providers.test.ts`)
- Modify: `src/SqlDesk.Web/src/formatSql.ts`, `sqlContext.ts`, `completion.ts` (crase)
- Test: `providers.test.ts`, `formatSql.test.ts`, `sqlContext` (arquivo de teste existente, se houver; senão `suggest.test.ts`)

**Interfaces:**
- Produces `providers.ts`:
```ts
export type ProviderId = 'sqlserver' | 'mysql'
export interface ProviderInfo { id: ProviderId; name: string; defaultPort: number; encryptLabel: string; trustLabel: string; quote(ident: string): string }
export const PROVIDERS: Record<ProviderId, ProviderInfo>
export const providerOf = (s: { provider?: string }): ProviderInfo
export const withProvider = (s: ConnectionSettings, id: ProviderId): ConnectionSettings   // troca o provedor ajustando encrypt/trust padrão
```
  - `PROVIDERS.mysql.quote('a`b') === '`a``b`'`; `PROVIDERS.sqlserver.quote('a]b') === '[a]]b]'`.
  - `trustLabel` do MySQL: "Aceitar certificado não verificado (SslMode=Required)"; `encryptLabel`: "Criptografar a conexão (SSL)".

- [ ] **Step 1: Testes vitest que falham** (`providers.test.ts`: `quote`, `providerOf` com `provider` ausente → SQL Server, `withProvider` zera `advanced` e mantém nome/usuário; `formatSql.test.ts`: formatar `select \`a\`.\`b\` from \`t\`` no dialeto MySQL preserva crases e não coloca `[ ]`; `sqlContext` reconhece `` `schema`.`tabela` `` e `` `alias` ``).

- [ ] **Step 2: Rodar e ver falhar** (`cd src/SqlDesk.Web && npm test --silent`).

- [ ] **Step 3: Implementar**
  - `ConnectionDialog`: seletor "Tipo de servidor" (SQL Server | MySQL / MariaDB) no topo; ao trocar, `setSettings(withProvider(...))`; rótulos de `encrypt`/`trust` vêm de `providerOf(settings)`; no MySQL o campo servidor aceita `host:porta` e o placeholder muda; a lista de chaves avançadas segue igual. O botão "Confiar no certificado e testar de novo" continua para `certificateUntrusted`.
  - `Sidebar`/`StatusBar`: mostrar `providerOf(conn.settings).name` ao lado do servidor/banco (texto curto "MySQL" / "SQL Server").
  - `GuardDialogs`: quando `ExecuteResponse.irreversible`, a primeira confirmação mostra um bloco de aviso forte ("Este comando muda a estrutura do banco e **não pode ser desfeito**: o MySQL confirma DDL sozinho. Não haverá commit/rollback depois."), botão rotulado "Executar mesmo assim"; `GuardStatus 'completed'` no `App.tsx` encerra a execução como uma execução normal (sem abrir a segunda confirmação). Rótulo de `DangerKind 'alterTable'` em `guardDiff.ts`/`GuardDialogs.tsx` ("ALTER TABLE").
  - `formatSql.ts`: o formatador recebe o provedor da aba; para `mysql` usa o dialeto `mysql` da biblioteca já usada (abrir `formatSql.ts` e ver qual lib e como escolhe o dialeto; se só suportar T-SQL, desativar a formatação para abas MySQL com mensagem na barra de status em vez de corromper o SQL, preservando `formatSql.safety.test.ts`).
  - `sqlContext.ts`/`completion.ts`: aceitar `` `ident` `` no `IDENT` (regex e `unquote`), mascarar comentários `#` e `-- ` e crases ao calcular statements; no MySQL, `;` ainda separa e `GO` não é separador (passar `provider` para `statementBoundaries`).

- [ ] **Step 4: Suítes** (`npm test --silent`, `npm run build`, `npm run lint` se existir, `dotnet test SqlDesk.sln`). Expected: passam.

- [ ] **Step 5: Verificar na interface com o app real** (skill `run`): publicar/rodar o Host, cadastrar uma conexão MySQL apontando para o container, executar `SELECT 1`, `DELETE FROM g1` (ver as duas confirmações) e `DROP TABLE` (ver o aviso irreversível). Registrar o que viu.

- [ ] **Step 6: Commit**

```bash
git add -A src
git commit -m "Adiciona o seletor de MySQL ao diálogo de conexão e ajusta as confirmações das travas"
```

---

### Task 13: Teste de ponta a ponta no Docker e documentação

**Files:**
- Create: `tests/SqlDesk.Integration.Tests/ExecutionTests.cs`, `ExportTests.cs`
- Modify: `README.md`, `docs/decisoes.md`
- Modify: `docs/screenshot.png` (somente se o layout mudou; dados fictícios, Edge headless, como combinado)

**Interfaces:** consome o `Harness` (Task 9).

- [ ] **Step 1: Testes de integração dos fluxos principais (os dois servidores)**

```csharp
public class ExecutionIntegrationTests
{
    [IntegrationTheory, MemberData(nameof(TestServers.All), MemberType = typeof(TestServers))]
    public async Task Executa_select_e_devolve_linhas(string server)
    {
        await using var h = await Harness.OpenAsync(server);
        var result = await h.RunCapturingAsync("SELECT 1 AS a, 'x' AS b UNION ALL SELECT 2, 'y'");
        Assert.Equal(2, result.Sets.Single().Sample.Count);
        Assert.Equal(RunStatus.Completed, result.Summary.Status);
    }

    [IntegrationTheory, MemberData(nameof(TestServers.All), MemberType = typeof(TestServers))]
    public async Task Cancela_consulta_longa(string server)
    {
        await using var h = await Harness.OpenAsync(server);
        var run = h.RunCapturingAsync("SELECT SLEEP(30)");
        await Task.Delay(500);
        h.Runner.Cancel(h.TabId);
        var result = await run;
        Assert.Equal(RunStatus.Cancelled, result.Summary.Status);
    }

    [IntegrationTheory, MemberData(nameof(TestServers.All), MemberType = typeof(TestServers))]
    public async Task Erro_de_sintaxe_vira_mensagem_e_nao_excecao(string server)
    {
        await using var h = await Harness.OpenAsync(server);
        var result = await h.RunCapturingAsync("SELEC 1");
        Assert.Equal(RunStatus.Error, result.Summary.Status);
        Assert.Contains(result.Messages, m => m.Kind == MessageKinds.Error);
    }

    [IntegrationTheory, MemberData(nameof(TestServers.All), MemberType = typeof(TestServers))]
    public async Task Script_com_varios_statements_e_ponto_e_virgula_em_string(string server)
    {
        await using var h = await Harness.OpenAsync(server);
        var result = await h.RunCapturingAsync("SELECT ';' AS p; SELECT 2 AS q");
        Assert.Equal(2, result.Sets.Count);
    }
}
```

Exportação (`ExportTests`): `ExportByRerunAsync` de um `SELECT` com 20 000 linhas gerado por `WITH RECURSIVE` (`cte_max_recursion_depth` é 1000 no MySQL 8: usar `SET SESSION cte_max_recursion_depth = 100000;` no início do texto, que é aceito por `IsReadOnly` só se `SET @var`; então gerar as linhas com um `INSERT ... SELECT` de auto-join numa tabela de apoio criada pelo `Harness.RunAsync`) para CSV e XLSX; afirmar contagem de linhas do CSV. Estender o `Harness` com `RunCapturingAsync` (retorna `Summary`, `Sets` do `CapturingSink` e `Messages`).

- [ ] **Step 2: Rodar a integração nos dois containers.** Expected: passam. Falhas aqui são bugs reais dos Tasks anteriores: corrigir lá, com teste de regressão no nível certo, e voltar.

- [ ] **Step 3: README e decisões**

Atualizar `README.md` (linha 5 e seções de recursos/limitações): "Cliente desktop leve para consultar **SQL Server, MySQL e MariaDB**". Registrar em `docs/decisoes.md`: provedor por conexão; analisador léxico do MySQL (e que erra para o lado seguro); DDL sem rollback; contagem prévia no lugar do diff `OUTPUT`; sonda de transação (`@@in_transaction` / `performance_schema`) e o fallback rastreado; mapeamento `encrypt`/`trust` → `SslMode`; MyISAM sem transação (aviso pendente, ver abaixo); `GO` não existe no MySQL, `DELIMITER` sim. Se alguma pendência virar limitação (ex.: aviso de MyISAM não implementado), listar como limitação conhecida.

- [ ] **Step 4: Regenerar a imagem do README**, se o layout do diálogo/lista mudou (Edge headless, dados fictícios).

- [ ] **Step 5: Suítes finais**

Run: `dotnet test SqlDesk.sln --nologo -v q && dotnet test tests/SqlDesk.Integration.Tests --nologo -v q && (cd src/SqlDesk.Web && npm test --silent && npm run build --silent)`
Expected: tudo verde.

- [ ] **Step 6: Commit**

```bash
git add -A README.md docs tests
git commit -m "Cobre MySQL e MariaDB com testes de integração e documenta o suporte"
```

---

## Self-Review (feito contra a spec)

- §1 Camada de provedor → Tasks 1, 2, 3, 5, 6. §2 Conexão e interface → Tasks 7, 12 (desvio do `SslMode` declarado nas Global Constraints). §3 Analisador → Tasks 2, 4. §4 Travas → Tasks 8, 9. §5 Metadados → Task 10. §6 Exportação/tipos → Tasks 11, 13. §7 Testes/Docker/README → Tasks 0, 8, 9, 13.
- **Lacuna conhecida:** o aviso de MyISAM (spec §4) não tem task própria. Está listado em Task 13 como limitação documentada; se o usuário quiser o aviso, vira uma Task 9b (consulta `information_schema.TABLES.ENGINE` das tabelas-alvo antes de executar, com mensagem no `sink`).
- Tipos consistentes: `ISqlAnalyzer` (Task 2) usado em 3, 4, 5, 6, 9; `IDatabaseProvider`/`ITransactionSql`/`IMetadataSql` (Task 5) usados em 6, 7, 8, 9, 10; `GuardStatus.Completed` e `DangerKinds.IsDdl` (Task 9) usados em 9 e 12; `DangerKind.AlterTable` (Task 4) usado em 9 e 12.
- Pontos que dependem de comportamento real do driver/servidor e por isso só se confirmam nos testes de integração: `RecordsAffected` por statement no MySqlConnector (Task 6), a sonda de transação (Task 8), `InfoMessage` do MySqlConnector (Task 6). Se divergirem, o teste de integração decide e o código se ajusta.
