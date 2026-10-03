using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
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

    private static readonly Regex DestructiveWord =
        new(@"\b(UPDATE|DELETE|TRUNCATE|DROP|ALTER)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>O que pode vir depois de DROP num ALTER TABLE sem ser uma coluna.</summary>
    private static readonly string[] AlterDropNotColumn =
        ["INDEX", "KEY", "PRIMARY", "FOREIGN", "CHECK", "CONSTRAINT", "PARTITION", "DEFAULT", "SYSTEM", "PERIOD"];

    /// <summary>Palavras que aparecem num predicado sem referenciar coluna (literais e operadores).</summary>
    private static readonly HashSet<string> LiteralWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "TRUE", "FALSE", "NULL", "UNKNOWN", "AND", "OR", "XOR", "NOT", "IS", "IN", "LIKE", "BETWEEN", "REGEXP", "RLIKE",
        "DIV", "MOD", "ESCAPE",
    };

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
            // Core nulo (SET STATEMENT aninhado demais): conta como escrita, por segurança.
            writes |= Pieces(st).Any(p => Core(p) is not { } c || HasWrite(c));

            var found = ClassifyChunk(st, script, map);
            // As outras leituras rodam sempre, mesmo que a principal já tenha achado perigo: um DELETE perigoso não pode
            // servir de cobertura para um DROP que só existe na leitura do servidor (ex.: depois de um /*!99999 ' */).
            if (AlternateScans(text).Any(alt => HasUncoveredDanger(alt, text, st.Start, found)))
                found.Add(new DangerousStatement(DangerKind.Unanalyzable, st.Start, Math.Max(1, st.End - st.Start), map.LineOf(st.Start),
                    "Dependendo do servidor (NO_BACKSLASH_ESCAPES, ou comentário /*! /*M! /*+ que ele ignora), este trecho é lido de " +
                    "outro jeito (as aspas fecham em outro lugar ou o comando destrutivo atinge outro objeto); tratado como perigoso " +
                    "por segurança",
                    null, [], CanPreviewWithOutput: false));
            dangers.AddRange(found);
        }
        return new ScriptAnalysis(batches, dangers.OrderBy(d => d.Start).ToList(), [], [], writes);
    }

    /// <summary>
    /// A releitura <paramref name="alt"/> do trecho (que começa em <paramref name="offset"/> no script) tem algum perigo que a
    /// leitura principal não lista: mesmo tipo, posição, alvo e contagem prévia, ou dentro de um trecho que ela já trata como
    /// não analisável.
    /// Statements quebrados na releitura são ignorados (o servidor os recusaria com erro de sintaxe).
    /// </summary>
    private static bool HasUncoveredDanger(ScanResult alt, string text, int offset, List<DangerousStatement> main)
    {
        var map = new LineMap(text);
        foreach (var s in alt.Statements.Where(s => !s.Broken))
            foreach (var d in ClassifyChunk(s, text, map))
            {
                var at = offset + d.Start;
                // O alvo também precisa bater: um comentário executável pode trocar a tabela (DELETE FROM /*!99999 i */ m),
                // e a checagem de mecanismo e o diálogo olhariam a tabela errada.
                var covered = main.Any(m => (m.Kind == d.Kind && m.Start == at && string.Equals(m.Target, d.Target, StringComparison.Ordinal) &&
                                             m.CountQueries.SequenceEqual(d.CountQueries, StringComparer.Ordinal)) ||
                                            (m.Kind == DangerKind.Unanalyzable && m.Start <= at && at < m.Start + m.Length));
                if (!covered) return true;
            }
        return false;
    }

    public LocateResult Locate(string text, int cursor)
    {
        cursor = Math.Clamp(cursor, 0, text.Length);
        var scan = MySqlScanner.Scan(text);
        var hit = scan.Statements.LastOrDefault(s => cursor >= s.Start && cursor <= s.End)
                  // cursor depois do fim do statement, na mesma linha (ex.: logo após o ';')
                  ?? scan.Statements.LastOrDefault(s => s.End <= cursor && text.AsSpan(s.End, cursor - s.End).IndexOfAny('\n', '\r') < 0
                      && MySqlScanner.TrimSpace(text[s.End..cursor]).Trim(';').Length == 0);
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

    /// <summary>
    /// Primeira palavra dos comandos que confirmam a transação sozinhos no MySQL/MariaDB (DDL, controle de transação,
    /// travas, administração) ou que executam código que não dá para enxergar (CALL, EXECUTE, PREPARE): para esses,
    /// assume o pior. ROLLBACK, ANALYZE e SET têm regra própria.
    /// </summary>
    private static readonly HashSet<string> ImplicitCommitWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "CREATE", "ALTER", "DROP", "RENAME", "TRUNCATE",
        "START", "BEGIN", "COMMIT",
        "LOCK", "UNLOCK", "GRANT", "REVOKE",
        "FLUSH", "OPTIMIZE", "REPAIR", "CHECK", "CACHE", "LOAD", "RESET", "INSTALL", "UNINSTALL", "XA", "CHANGE", "STOP",
        "IMPORT", "CLONE",
        "CALL", "EXECUTE", "PREPARE",
    };

    public bool CausesImplicitCommit(string batchText)
    {
        var scan = MySqlScanner.Scan(batchText);
        if (StatementsCommit(scan)) return true;
        // As outras leituras possíveis do servidor (sem escape por barra, /*! como comentário) também contam.
        return AlternateScans(batchText).Any(StatementsCommit);
    }

    private static bool StatementsCommit(ScanResult scan)
    {
        foreach (var st in scan.Statements)
        {
            // Texto quebrado ou bloco de código (IF, WHILE, rótulo...): não dá para saber o que roda.
            if (st.Broken || IsCompound(st.Tokens)) return true;
            if (Pieces(st).Any(PieceCommits)) return true;
        }
        return false;
    }

    private static bool PieceCommits(IReadOnlyList<Token> t)
    {
        var f = t[0];
        if (f.Kind != TokenKind.Word) return false;
        if (ImplicitCommitWords.Contains(f.Text)) return true;
        // ROLLBACK [WORK] TO [SAVEPOINT] x só volta ao savepoint; o ROLLBACK comum encerra a transação.
        if (f.Is("ROLLBACK")) return !t.Skip(1).Take(2).Any(x => x.Is("TO"));
        // ANALYZE TABLE confirma; ANALYZE UPDATE/DELETE/SELECT (MariaDB) só executa o comando com estatísticas.
        if (f.Is("ANALYZE")) return t.Count > 1 && (t[1].Is("TABLE") || t[1].Is("TABLES") || t[1].Is("NO_WRITE_TO_BINLOG") || t[1].Is("LOCAL"));
        if (f.Is("SET"))
        {
            // MariaDB: SET STATEMENT var=valor[, ...] FOR <comando> executa o comando: vale o que ele for. Sem FOR (ou sem
            // comando depois dele) não dá para saber: assume o pior.
            if (t.Count > 1 && t[1].Is("STATEMENT"))
            {
                var peel = PeelSetStatement(t);
                // O comando interno já não começa com SET STATEMENT: esta chamada não se repete.
                return peel.Opaque || peel.TooDeep || peel.MentionsAutocommit || PieceCommits(Slice(t, peel.Start));
            }
            // SET PASSWORD e SET DEFAULT ROLE confirmam sozinhos (SET ROLE não).
            if (t.Count > 1 && (t[1].Is("PASSWORD") || (t[1].Is("DEFAULT") && t.Count > 2 && t[2].Is("ROLE")))) return true;
            // SET autocommit = 1 confirma a transação aberta; SET autocommit = 0 muda o modo da sessão.
            return t.Any(x => x.Text.Contains("autocommit", StringComparison.OrdinalIgnoreCase));
        }
        return false;
    }

    public bool IsReadOnly(string script, out string? reason)
    {
        var scan = MySqlScanner.Scan(script);
        if (scan.Broken) { reason = "o texto não pôde ser analisado (string, crase, comentário ou parênteses sem fechar)"; return false; }
        if (!ReadOnlyStatements(scan, out reason)) return false;
        // Cada batch também precisa ser só leitura nas outras leituras possíveis do servidor (aspas que fecham em outro lugar).
        foreach (var st in scan.Statements)
            foreach (var alt in AlternateScans(script[st.Start..st.End]))
                if (!ReadOnlyStatements(alt, out reason)) return false;
        return true;
    }

    /// <summary>
    /// Releituras do trecho do jeito que o servidor pode ler: sem escape por barra (NO_BACKSLASH_ESCAPES) e/ou com
    /// <c>/*!</c>, <c>/*M!</c> e <c>/*+</c> como comentário comum. Só as que podem mudar algo são feitas.
    /// </summary>
    private static IEnumerable<ScanResult> AlternateScans(string text)
    {
        var backslash = text.Contains('\\');
        var executable = text.Contains("/*!", StringComparison.Ordinal) || text.Contains("/*+", StringComparison.Ordinal) ||
                         text.Contains("/*M!", StringComparison.OrdinalIgnoreCase);
        if (backslash) yield return MySqlScanner.Scan(text, backslashEscapes: false);
        if (executable) yield return MySqlScanner.Scan(text, executableComments: false);
        if (backslash && executable) yield return MySqlScanner.Scan(text, backslashEscapes: false, executableComments: false);
    }

    /// <summary>
    /// Todos os statements bem formados da varredura são só leitura. Statement quebrado é ignorado aqui: na leitura
    /// principal ele já recusou; numa releitura, o servidor o recusaria com erro de sintaxe.
    /// </summary>
    private static bool ReadOnlyStatements(ScanResult scan, out string? reason)
    {
        reason = null;
        foreach (var st in scan.Statements.Where(s => !s.Broken))
        {
            if (IsCompound(st.Tokens)) { reason = "contém um bloco de código (BEGIN NOT ATOMIC, IF, WHILE...)"; return false; }
            foreach (var t in Pieces(st))
            {
                var first = t[0];
                var ok = first.Is("SELECT") || first.Is("SHOW") || first.Is("DESCRIBE") || first.Is("DESC") || first.Is("EXPLAIN") ||
                         first.Is("USE") || first.Is("WITH") || (first.Is("SET") && SetsOnlyUserVariables(t));
                if (!ok) { reason = $"contém {first.Text.ToUpperInvariant()}, que não é só leitura"; return false; }
                var describes = first.Is("WITH") || first.Is("EXPLAIN") || first.Is("DESCRIBE") || first.Is("DESC");
                for (var i = 0; i < t.Count; i++)
                {
                    var x = t[i];
                    if (x.Is("INTO")) { reason = "contém SELECT ... INTO"; return false; }
                    if (x.Is("FOR") && i + 1 < t.Count && (t[i + 1].Is("UPDATE") || t[i + 1].Is("SHARE"))) { reason = "contém FOR UPDATE/SHARE (trava linhas)"; return false; }
                    if (x.Is("LOCK") && i + 1 < t.Count && t[i + 1].Is("IN")) { reason = "contém LOCK IN SHARE MODE (trava linhas)"; return false; }
                    // EXPLAIN ANALYZE / ANALYZE executam o comando; WITH pode terminar em UPDATE/DELETE.
                    if (describes && (x.Is("UPDATE") || x.Is("DELETE") || x.Is("INSERT") || x.Is("REPLACE"))) { reason = $"contém {x.Text.ToUpperInvariant()}"; return false; }
                }
            }
        }
        return true;
    }

    /// <summary>SET só com variáveis de usuário (<c>@x</c>); <c>@@global</c>, <c>sql_mode</c> etc. mudam o servidor/sessão.</summary>
    private static bool SetsOnlyUserVariables(IReadOnlyList<Token> t)
    {
        var depth = 0;
        var expectTarget = true;
        for (var i = 1; i < t.Count; i++)
        {
            if (t[i].IsSymbol("(")) depth++;
            else if (t[i].IsSymbol(")")) depth--;
            if (depth != 0) continue;
            if (expectTarget)
            {
                if (t[i].Kind != TokenKind.Word || !t[i].Text.StartsWith('@') || t[i].Text.StartsWith("@@", StringComparison.Ordinal)) return false;
                expectTarget = false;
            }
            else if (t[i].IsSymbol(",")) expectTarget = true;
        }
        return !expectTarget;
    }

    private static bool HasWrite(IReadOnlyList<Token> t) =>
        t.Count > 0 && (t[0].Is("INSERT") || t[0].Is("UPDATE") || t[0].Is("DELETE") || t[0].Is("REPLACE"));

    /// <summary>
    /// Com um terminador customizado, o <c>;</c> fica dentro do statement mas o servidor (com múltiplos statements)
    /// executa cada parte: cada pedaço é analisado como um statement. Num <c>CREATE PROCEDURE ... BEGIN ... END</c>
    /// isso pode acusar comandos do corpo (que só rodam na chamada) — erro para o lado seguro.
    /// </summary>
    private static List<IReadOnlyList<Token>> Pieces(MySqlStatement st)
    {
        var pieces = new List<IReadOnlyList<Token>>();
        var cur = new List<Token>();
        foreach (var t in st.Tokens)
        {
            if (t.IsSymbol(";")) { if (cur.Count > 0) pieces.Add(cur); cur = []; }
            else cur.Add(t);
        }
        if (cur.Count > 0) pieces.Add(cur);
        return pieces;
    }

    /// <summary>MariaDB executa blocos fora de procedures: <c>BEGIN NOT ATOMIC</c>, <c>IF</c>, <c>WHILE</c>, <c>rotulo: ...</c>.</summary>
    private static bool IsCompound(IReadOnlyList<Token> t)
    {
        if (t.Count == 0) return false;
        var f = t[0];
        if (f.Is("BEGIN")) return t.Count > 2 && t[1].Is("NOT") && t[2].Is("ATOMIC");
        if (f.Is("IF") || f.Is("CASE") || f.Is("LOOP") || f.Is("REPEAT") || f.Is("WHILE") || f.Is("FOR")) return true;
        return f.Kind is TokenKind.Word or TokenKind.Quoted && t.Count > 1 && t[1].IsSymbol(":");
    }

    /// <summary>Máximo de <c>SET STATEMENT ... FOR</c> aninhados analisados; acima disso o trecho é tratado como opaco.</summary>
    internal const int MaxSetStatementNesting = 32;

    /// <param name="Start">Índice, nos tokens do pedaço, do comando que de fato executa.</param>
    /// <param name="Opaque">Faltou o FOR ou o comando depois dele: não dá para saber o que roda.</param>
    /// <param name="TooDeep">Mais de <see cref="MaxSetStatementNesting"/> níveis: tratado como opaco.</param>
    /// <param name="MentionsAutocommit">Alguma lista de variáveis antes de um FOR menciona autocommit.</param>
    private readonly record struct SetStatementPeel(int Start, bool Opaque, bool TooDeep, bool MentionsAutocommit);

    /// <summary>
    /// Tira os prefixos <c>SET STATEMENT var=valor[, ...] FOR</c> (MariaDB) num laço, avançando um índice: custo linear e
    /// sem recursão, para texto colado ou gerado com milhares de níveis não estourar a pilha.
    /// </summary>
    private static SetStatementPeel PeelSetStatement(IReadOnlyList<Token> t)
    {
        var i = 0;
        var levels = 0;
        var autocommit = false;
        while (i + 1 < t.Count && t[i].Is("SET") && t[i + 1].Is("STATEMENT"))
        {
            if (++levels > MaxSetStatementNesting) return new(i, false, true, autocommit);
            var depth = 0;
            var forAt = -1;
            for (var j = i + 2; j < t.Count && forAt < 0; j++)
            {
                if (t[j].IsSymbol("(")) depth++;
                else if (t[j].IsSymbol(")")) depth--;
                else if (depth == 0 && t[j].Is("FOR")) forAt = j;
                else if (t[j].Text.Contains("autocommit", StringComparison.OrdinalIgnoreCase)) autocommit = true;
            }
            if (forAt < 0 || forAt + 1 >= t.Count) return new(i, true, false, autocommit);
            i = forAt + 1;
        }
        return new(i, false, false, autocommit);
    }

    private static IReadOnlyList<Token> Slice(IReadOnlyList<Token> t, int start) => start == 0 ? t : t.Skip(start).ToList();

    /// <summary>
    /// O comando que de fato executa: em <c>SET STATEMENT ... FOR &lt;comando&gt;</c> (MariaDB) é o comando depois do
    /// último FOR; em <c>WITH ... UPDATE/DELETE</c>, <c>ANALYZE UPDATE/DELETE</c> (MariaDB) e
    /// <c>EXPLAIN|DESC|DESCRIBE ... ANALYZE UPDATE/DELETE</c> (MySQL 8) começa no primeiro UPDATE/DELETE no nível 0.
    /// <c>EXPLAIN</c> sem ANALYZE só mostra o plano e não executa. null = SET STATEMENT aninhado demais (opaco).
    /// </summary>
    private static IReadOnlyList<Token>? Core(IReadOnlyList<Token> t)
    {
        if (t.Count == 0) return t;
        if (t.Count > 1 && t[0].Is("SET") && t[1].Is("STATEMENT"))
        {
            var peel = PeelSetStatement(t);
            if (peel.TooDeep) return null;
            // Sem FOR (ou sem comando depois dele) o servidor recusa com erro de sintaxe: fica como está.
            if (!peel.Opaque) t = Slice(t, peel.Start);
        }
        var f = t[0];
        var prefixed = f.Is("WITH") || (f.Is("ANALYZE") && !(t.Count > 1 && t[1].Is("TABLE")));
        var describes = f.Is("EXPLAIN") || f.Is("DESC") || f.Is("DESCRIBE");
        if (!prefixed && !describes) return t;
        var depth = 0;
        var analyze = false;
        for (var i = 1; i < t.Count; i++)
        {
            if (t[i].IsSymbol("(")) depth++;
            else if (t[i].IsSymbol(")")) depth--;
            else if (depth != 0) continue;
            else if (t[i].Is("ANALYZE")) analyze = true;
            else if ((t[i].Is("UPDATE") && !t[i - 1].Is("FOR")) || t[i].Is("DELETE"))
                return prefixed || analyze ? t.Skip(i).ToList() : t;
        }
        return t;
    }

    private static List<DangerousStatement> ClassifyChunk(MySqlStatement st, string script, LineMap map)
    {
        var result = new List<DangerousStatement>();
        var length = Math.Max(1, st.End - st.Start);

        if (st.Broken)
        {
            // O texto cru inclui o que está dentro da string/crase/comentário aberto: na dúvida, conta.
            var m = DestructiveWord.Match(script, st.Start, st.End - st.Start);
            if (m.Success)
                result.Add(new DangerousStatement(DangerKind.Unanalyzable, st.Start, length, map.LineOf(st.Start),
                    $"Não foi possível analisar este trecho (string, crase, comentário ou parênteses sem fechar, ou DELIMITER malformado) " +
                    $"e ele contém {m.Value.ToUpperInvariant()}; tratado como perigoso por segurança",
                    null, [], CanPreviewWithOutput: false));
            return result;
        }

        if (IsCompound(st.Tokens))
        {
            var kw = st.Tokens.FirstOrDefault(x => Destructive.Any(x.Is));
            if (kw is not null)
                result.Add(new DangerousStatement(DangerKind.Unanalyzable, st.Start, length, map.LineOf(st.Start),
                    $"Bloco de código contém {kw.Text.ToUpperInvariant()}; não dá para analisar cada comando dele, tratado como perigoso por segurança",
                    null, [], CanPreviewWithOutput: false));
            return result;
        }

        foreach (var piece in Pieces(st))
            if (Classify(piece, map) is { } d) result.Add(d);
        return result;
    }

    private static DangerousStatement? Classify(IReadOnlyList<Token> piece, LineMap map)
    {
        if (piece.Count == 0) return null;
        var start = piece[0].Start;
        var line = map.LineOf(start);
        var length = Math.Max(1, piece[^1].End - start);
        var core = Core(piece);

        DangerousStatement D(DangerKind kind, string? target, string why, IReadOnlyList<string>? counts = null) =>
            new(kind, start, length, line, why, target, counts ?? [], CanPreviewWithOutput: false);

        if (core is not { } t)
            return D(DangerKind.Unanalyzable, null,
                $"Mais de {MaxSetStatementNesting} SET STATEMENT ... FOR aninhados: não dá para analisar o comando de dentro; " +
                "tratado como perigoso por segurança");

        var first = t[0];

        if (first.Is("UPDATE") || first.Is("DELETE"))
        {
            var isUpdate = first.Is("UPDATE");
            var target = TargetOf(t, isUpdate, out var multi);
            var whereAt = TopLevelIndex(t, "WHERE");
            var reason = whereAt < 0 ? "sem WHERE" : FiltersNothing(t, whereAt + 1) ? "com WHERE que não filtra nenhuma linha (não referencia colunas ou é sempre verdadeiro)" : null;
            if (reason is null) return null;
            var kind = isUpdate ? DangerKind.UpdateWithoutWhere : DangerKind.DeleteWithoutWhere;
            if (multi)
                return D(kind, null, isUpdate
                    ? $"UPDATE com mais de uma tabela {reason} vai afetar todas as linhas das tabelas alteradas"
                    : $"DELETE com mais de uma tabela {reason} vai apagar todas as linhas das tabelas indicadas");
            var shown = target ?? "a tabela";
            return D(kind, target,
                isUpdate ? $"UPDATE em {shown} {reason} vai afetar a tabela inteira" : $"DELETE em {shown} {reason} vai apagar todas as linhas da tabela",
                target is null ? [] : [$"SELECT COUNT(*) FROM {target}"]);
        }

        if (first.Is("TRUNCATE"))
        {
            var target = NameAfter(t, t.Count > 1 && t[1].Is("TABLE") ? 2 : 1);
            return D(DangerKind.TruncateTable, target, $"TRUNCATE TABLE em {target ?? "uma tabela"} remove todas as linhas da tabela",
                target is null ? [] : [$"SELECT COUNT(*) FROM {target}"]);
        }

        if (first.Is("DROP"))
        {
            var k = 1;
            if (k < t.Count && t[k].Is("TEMPORARY")) k++;
            var what = k < t.Count ? t[k].Text.ToUpperInvariant() : "objeto";
            k++;
            if (k + 1 < t.Count && t[k].Is("IF") && t[k + 1].Is("EXISTS")) k += 2;
            var target = NameAfter(t, k);
            IReadOnlyList<string> counts = what == "TABLE" && target is not null ? [$"SELECT COUNT(*) FROM {target}"] : [];
            return D(DangerKind.Drop, target, $"DROP {what} {target ?? Unnamed} apaga o objeto de forma definitiva", counts);
        }

        if (first.Is("ALTER"))
        {
            var isTable = t.Count > 1 && t[1].Is("TABLE");
            var nameAt = 2;
            if (isTable && nameAt + 1 < t.Count && t[nameAt].Is("IF") && t[nameAt + 1].Is("EXISTS")) nameAt += 2;
            var target = isTable ? NameAfter(t, nameAt) : null;
            var dropsColumn = false;
            var dropsPartition = false;
            if (isTable)
            {
                var depth = 0;
                for (var i = 2; i < t.Count; i++)
                {
                    if (t[i].IsSymbol("(")) depth++;
                    else if (t[i].IsSymbol(")")) depth--;
                    else if (depth == 0 && (t[i].Is("DROP") || t[i].Is("TRUNCATE")) && i + 1 < t.Count)
                    {
                        if (t[i + 1].Is("PARTITION")) dropsPartition = true;
                        else if (t[i].Is("DROP") && !AlterDropNotColumn.Any(t[i + 1].Is)) dropsColumn = true;
                    }
                }
            }
            if (dropsColumn) return D(DangerKind.DropColumn, target, $"ALTER TABLE em {target ?? Unnamed} remove coluna(s) e os dados delas");
            var subject = isTable ? "TABLE " + (target ?? Unnamed) : t.Count > 1 ? t[1].Text.ToUpperInvariant() : "";
            return D(DangerKind.AlterTable, target, dropsPartition
                ? $"ALTER {subject} remove partição(ões) e as linhas delas"
                : $"ALTER {subject} muda a estrutura do banco");
        }

        // MariaDB: CREATE OR REPLACE TABLE apaga a tabela existente (e os dados) antes de criar.
        if (first.Is("CREATE") && t.Count > 3 && t[1].Is("OR") && t[2].Is("REPLACE") &&
            (t[3].Is("TABLE") || (t[3].Is("TEMPORARY") && t.Count > 4 && t[4].Is("TABLE"))))
        {
            var target = NameAfter(t, t[3].Is("TABLE") ? 4 : 5);
            return D(DangerKind.Drop, target, $"CREATE OR REPLACE TABLE {target ?? Unnamed} apaga a tabela existente e os dados dela",
                target is null ? [] : [$"SELECT COUNT(*) FROM {target}"]);
        }

        // CREATE EVENT ... DO <corpo>: com o agendador ligado (padrão no MySQL 8), "AT CURRENT_TIMESTAMP" roda o corpo na hora.
        if (first.Is("CREATE") && IsEventDefinition(t))
        {
            var doAt = TopLevelIndex(t, "DO");
            if (doAt < 0 || doAt + 1 >= t.Count) return null;
            var body = t.Skip(doAt + 1).ToList();
            if (IsCompound(body) || body[0].Is("BEGIN"))
            {
                var kw = body.FirstOrDefault(x => Destructive.Any(x.Is));
                return kw is null ? null : D(DangerKind.Unanalyzable, null,
                    $"Evento agendado com bloco de código que contém {kw.Text.ToUpperInvariant()}; tratado como perigoso por segurança");
            }
            return Classify(body, map) is { } inner
                ? D(inner.Kind, inner.Target, $"Evento agendado executa: {inner.Description}", inner.CountQueries)
                : null;
        }

        // PREPARE s FROM '...': o comando está dentro da string; analisa o conteúdo dela.
        if (first.Is("PREPARE") && t.Count > 3 && t[2].Is("FROM") && IsStringLiteral(t[3]))
        {
            var (sql, _) = ConcatenatedLiteral(t, 3);
            var inner = Instance.Analyze(sql).Dangers;
            if (inner.Count > 0)
                return D(inner[0].Kind, inner[0].Target, $"PREPARE de um comando perigoso: {inner[0].Description}", inner[0].CountQueries);
        }

        // EXECUTE IMMEDIATE '...' (MariaDB) executa o texto na hora. Se não for literal, não há como saber o que roda.
        if (first.Is("EXECUTE") && t.Count > 1 && t[1].Is("IMMEDIATE"))
        {
            if (t.Count > 2 && IsStringLiteral(t[2]))
            {
                var (sql, next) = ConcatenatedLiteral(t, 2);
                if (next >= t.Count || t[next].Is("USING"))
                {
                    var inner = Instance.Analyze(sql).Dangers;
                    return inner.Count == 0 ? null
                        : D(inner[0].Kind, inner[0].Target, $"EXECUTE IMMEDIATE de um comando perigoso: {inner[0].Description}", inner[0].CountQueries);
                }
            }
            return D(DangerKind.Unanalyzable, null,
                "EXECUTE IMMEDIATE com o comando montado em variável ou expressão não pode ser analisado; tratado como perigoso por segurança");
        }
        return null;
    }

    private static bool IsStringLiteral(Token t) => t.Kind == TokenKind.Quoted && t.Text[0] != '`';

    /// <summary>Junta literais vizinhos (<c>'DR' 'OP TABLE t'</c> é uma string só no MySQL). Devolve o texto e o índice seguinte.</summary>
    private static (string Sql, int Next) ConcatenatedLiteral(IReadOnlyList<Token> t, int from)
    {
        var sb = new StringBuilder();
        var i = from;
        for (; i < t.Count && IsStringLiteral(t[i]); i++) sb.Append(Unquote(t[i].Text));
        return (sb.ToString(), i);
    }

    /// <summary>CREATE [DEFINER = ...] EVENT: a primeira palavra de tipo de objeto depois de CREATE é EVENT.</summary>
    private static bool IsEventDefinition(IReadOnlyList<Token> t)
    {
        string[] kinds = ["EVENT", "TABLE", "VIEW", "PROCEDURE", "FUNCTION", "TRIGGER", "INDEX", "DATABASE", "SCHEMA", "USER", "ROLE", "SERVER", "TABLESPACE", "SEQUENCE"];
        var k = t.Skip(1).FirstOrDefault(x => kinds.Any(x.Is));
        return k is not null && k.Is("EVENT");
    }

    private static string Unquote(string quoted)
    {
        var q = quoted[0];
        var end = quoted.Length > 1 && quoted[^1] == q ? quoted.Length - 1 : quoted.Length;
        var sb = new StringBuilder();
        for (var i = 1; i < end; i++)
        {
            var c = quoted[i];
            if (c == '\\' && i + 1 < end) { sb.Append(quoted[++i]); continue; }
            if (c == q && i + 1 < end && quoted[i + 1] == q) i++;
            sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>Índice da palavra no nível 0 de parênteses, ou -1.</summary>
    private static int TopLevelIndex(IReadOnlyList<Token> t, string word)
    {
        var depth = 0;
        for (var i = 0; i < t.Count; i++)
        {
            if (t[i].IsSymbol("(")) depth++;
            else if (t[i].IsSymbol(")")) depth--;
            else if (depth == 0 && t[i].Is(word)) return i;
        }
        return -1;
    }

    /// <summary>
    /// O predicado depois do WHERE não filtra: algum termo de OR (no nível 0) não referencia coluna — só literais e
    /// operadores, como <c>1</c>, <c>1=1</c>, <c>TRUE</c>, <c>'x'='x'</c>.
    /// </summary>
    private static bool FiltersNothing(IReadOnlyList<Token> t, int from)
    {
        var end = t.Count;
        var depth = 0;
        for (var i = from; i < t.Count; i++)
        {
            if (t[i].IsSymbol("(")) depth++;
            else if (t[i].IsSymbol(")")) depth--;
            else if (depth == 0 && (t[i].Is("ORDER") || t[i].Is("LIMIT") || t[i].Is("RETURNING"))) { end = i; break; }
        }
        var pred = t.Skip(from).Take(end - from).ToList();
        return pred.Count == 0 || IsTautology(pred);
    }

    /// <summary>
    /// Algum termo de OR/|| no nível 0 não referencia coluna. Um termo todo entre parênteses é aberto e analisado do
    /// mesmo jeito, para pegar <c>(id = 5 OR 1=1)</c>.
    /// </summary>
    private static bool IsTautology(List<Token> pred)
    {
        var terms = new List<List<Token>> { new() };
        var depth = 0;
        for (var i = 0; i < pred.Count; i++)
        {
            var x = pred[i];
            if (x.IsSymbol("(")) depth++;
            else if (x.IsSymbol(")")) depth--;
            if (depth == 0 && (x.Is("OR") || (x.IsSymbol("|") && i + 1 < pred.Count && pred[i + 1].IsSymbol("|") && pred[i + 1].Start == x.End)))
            {
                if (!x.Is("OR")) i++;
                terms.Add([]);
                continue;
            }
            terms[^1].Add(x);
        }
        return terms.Any(term => term.Count == 0 || !term.Where((x, i) => IsColumnLike(term, i)).Any() ||
                                 (WrappedInParens(term) && IsTautology(term.GetRange(1, term.Count - 2))));
    }

    /// <summary>O termo começa com "(" e o ")" que fecha esse parêntese é o último token.</summary>
    private static bool WrappedInParens(List<Token> term)
    {
        if (term.Count < 2 || !term[0].IsSymbol("(") || !term[^1].IsSymbol(")")) return false;
        var depth = 0;
        for (var i = 0; i < term.Count; i++)
        {
            if (term[i].IsSymbol("(")) depth++;
            else if (term[i].IsSymbol(")") && --depth == 0) return i == term.Count - 1;
        }
        return false;
    }

    /// <summary>Token que pode ser coluna/função: palavra que não é literal nem operador, ou identificador entre crases.</summary>
    private static bool IsColumnLike(IReadOnlyList<Token> t, int i)
    {
        var x = t[i];
        if (x.Kind == TokenKind.Quoted) return x.Text[0] == '`';
        if (x.Kind != TokenKind.Word) return false;
        if (LiteralWords.Contains(x.Text)) return false;
        if (double.TryParse(x.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out _)) return false;
        if (Regex.IsMatch(x.Text, "^(0x[0-9a-fA-F]+|0b[01]+)$")) return false;
        // Prefixos de literal colados numa string: x'1F', b'01', N'abc', _utf8mb4'abc'.
        if (i + 1 < t.Count && t[i + 1].Kind == TokenKind.Quoted && t[i + 1].Start == x.End && t[i + 1].Text[0] == '\'' &&
            ((x.Text.Length == 1 && "xXbBnN".Contains(x.Text[0])) || x.Text.StartsWith('_'))) return false;
        return true;
    }

    /// <summary>Palavras que, na lista de tabelas, juntam uma segunda tabela.</summary>
    private static readonly string[] JoinWords = ["JOIN", "STRAIGHT_JOIN", "INNER", "LEFT", "RIGHT", "CROSS", "NATURAL", "OUTER"];

    /// <summary>
    /// Alvo de UPDATE (palavra logo após UPDATE, pulando LOW_PRIORITY/IGNORE) ou DELETE (após FROM, pulando
    /// LOW_PRIORITY/QUICK/IGNORE). Com mais de uma tabela (<c>UPDATE a, b</c>, <c>UPDATE a JOIN b</c>,
    /// <c>DELETE x FROM a JOIN x</c>, <c>DELETE FROM x USING ...</c>, <c>DELETE FROM a, b</c>) não há um alvo só: devolve
    /// null com <paramref name="multi"/> verdadeiro (sem contagem e, no MySQL/MariaDB, irreversível por alvo não reconhecido,
    /// porque qualquer uma das tabelas pode não ser transacional).
    /// </summary>
    private static string? TargetOf(IReadOnlyList<Token> t, bool isUpdate, out bool multi)
    {
        multi = false;
        var i = 1;
        if (isUpdate)
        {
            while (i < t.Count && (t[i].Is("LOW_PRIORITY") || t[i].Is("IGNORE"))) i++;
            var setAt = TopLevelIndex(t, "SET");
            if (JoinsTables(t, i, setAt < 0 ? t.Count : setAt)) { multi = true; return null; }
            return NameAfter(t, i);
        }
        // HISTORY: DELETE HISTORY FROM t (MariaDB, tabelas versionadas) apaga só da própria tabela.
        while (i < t.Count && (t[i].Is("LOW_PRIORITY") || t[i].Is("QUICK") || t[i].Is("IGNORE") || t[i].Is("HISTORY"))) i++;
        var from = TopLevelIndex(t, "FROM");
        if (from < 0) return null;
        // Algo entre o DELETE e o FROM é a lista de tabelas do DELETE multi-tabela (DELETE t1, t2 FROM ...).
        if (from != i) { multi = true; return null; }
        var end = t.Count;
        var depth = 0;
        for (var k = from + 1; k < t.Count; k++)
        {
            if (t[k].IsSymbol("(")) depth++;
            else if (t[k].IsSymbol(")")) depth--;
            else if (depth == 0 && (t[k].Is("WHERE") || t[k].Is("ORDER") || t[k].Is("LIMIT") || t[k].Is("RETURNING"))) { end = k; break; }
        }
        if (JoinsTables(t, from + 1, end) || IndexAtTopLevel(t, from + 1, end, "USING") >= 0) { multi = true; return null; }
        return NameAfter(t, from + 1);
    }

    /// <summary>Entre <paramref name="from"/> e <paramref name="end"/> (lista de tabelas), há <c>,</c> ou JOIN no nível 0, ou ela começa com parêntese.</summary>
    private static bool JoinsTables(IReadOnlyList<Token> t, int from, int end)
    {
        if (from < end && t[from].IsSymbol("(")) return true;
        var depth = 0;
        for (var k = from; k < end; k++)
        {
            if (t[k].IsSymbol("(")) depth++;
            else if (t[k].IsSymbol(")")) depth--;
            else if (depth == 0 && (t[k].IsSymbol(",") || JoinWords.Any(t[k].Is))) return true;
        }
        return false;
    }

    private static int IndexAtTopLevel(IReadOnlyList<Token> t, int from, int end, string word)
    {
        var depth = 0;
        for (var k = from; k < end; k++)
        {
            if (t[k].IsSymbol("(")) depth++;
            else if (t[k].IsSymbol(")")) depth--;
            else if (depth == 0 && t[k].Is(word)) return k;
        }
        return -1;
    }

    /// <summary>Nome possivelmente qualificado (<c>db.tabela</c>, com crases) a partir de <paramref name="index"/>.</summary>
    /// <remarks>
    /// O nome vira texto da contagem prévia (<c>SELECT COUNT(*) FROM nome</c>) que o app executa: só palavra de
    /// identificador ou nome entre crases (com crases internas dobradas) servem. String (<c>'x'</c>, <c>"x"</c>, que com
    /// NO_BACKSLASH_ESCAPES/ANSI_QUOTES fecham em outro lugar), variável ou qualquer outra coisa: null (sem contagem).
    /// </remarks>
    private const string Unnamed = "(nome não reconhecido)";

    private static string? NameAfter(IReadOnlyList<Token> t, int index)
    {
        if (index >= t.Count || !IsIdentifierPart(t[index])) return null;
        var name = t[index].Text;
        var i = index + 1;
        while (i + 1 < t.Count && t[i].IsSymbol(".") && t[i + 1].Kind is TokenKind.Word or TokenKind.Quoted)
        {
            if (!IsIdentifierPart(t[i + 1])) return null;
            name += "." + t[i + 1].Text;
            i += 2;
        }
        return name;
    }

    /// <summary>Palavra só com <c>[A-Za-z0-9_$]</c> e caracteres não ASCII, ou <c>`...`</c> com crases internas dobradas.</summary>
    private static bool IsIdentifierPart(Token x)
    {
        if (x.Kind == TokenKind.Word)
            return x.Text.Length > 0 && x.Text.All(c => char.IsAsciiLetterOrDigit(c) || c == '_' || c == '$' || c > 127);
        if (x.Kind != TokenKind.Quoted || x.Text.Length < 2 || x.Text[0] != '`' || x.Text[^1] != '`') return false;
        var inner = x.Text[1..^1];
        return inner.Length > 0 && !inner.Replace("``", "").Contains('`');
    }

    private static bool LineIsBlank(string text, int cursor)
    {
        var s = cursor; while (s > 0 && text[s - 1] != '\n' && text[s - 1] != '\r') s--;
        var e = cursor; while (e < text.Length && text[e] != '\n' && text[e] != '\r') e++;
        for (var i = s; i < e; i++) if (!MySqlScanner.IsSpace(text[i])) return false;
        return true;
    }
}
