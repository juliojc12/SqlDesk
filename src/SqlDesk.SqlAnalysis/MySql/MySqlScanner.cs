namespace SqlDesk.SqlAnalysis.MySql;

internal enum TokenKind { Word, Quoted, Symbol, Terminator }

/// <summary><paramref name="Text"/> é o texto original; para Quoted inclui as aspas/crases.</summary>
internal sealed record Token(TokenKind Kind, int Start, int Length, string Text)
{
    public int End => Start + Length;

    public bool Is(string word) => Kind == TokenKind.Word && Text.Equals(word, StringComparison.OrdinalIgnoreCase);

    public bool IsSymbol(string s) => Kind == TokenKind.Symbol && Text == s;
}

/// <summary>Resultado da varredura: tokens de código, statements (listas de tokens) e se houve erro léxico.</summary>
internal sealed record ScanResult(IReadOnlyList<Token> Tokens, IReadOnlyList<MySqlStatement> Statements, bool Broken);

/// <summary>
/// Um statement (texto entre terminadores). <see cref="Broken"/> indica que ele não pôde ser lido com segurança:
/// string/crase/comentário sem fechar, parênteses desbalanceados ou <c>DELIMITER</c> malformado.
/// </summary>
internal sealed record MySqlStatement(int Start, int End, IReadOnlyList<Token> Tokens, bool Broken);

/// <summary>
/// Varredura léxica de MySQL/MariaDB: sabe o que é código, string, identificador entre crase e comentário, e entende
/// <c>DELIMITER</c>. Não é um parser: serve para separar statements sem se enganar com <c>;</c> dentro de strings e comentários.
/// </summary>
internal static class MySqlScanner
{
    /// <param name="backslashEscapes">
    /// <c>false</c> simula o modo <c>NO_BACKSLASH_ESCAPES</c> do servidor, em que <c>\</c> não escapa nada dentro de strings.
    /// </param>
    /// <param name="executableComments">
    /// <c>false</c> lê <c>/*!</c> e <c>/*M!</c> como comentário comum: é o que o servidor faz quando a versão é maior que a
    /// dele ou quando o MySQL vê <c>/*M!</c>. Dicas de otimizador (<c>/*+ ... */</c>) são sempre comentário comum: o servidor
    /// nunca as executa como SQL.
    /// </param>
    /// <param name="gates">
    /// Se não for null, recebe a abertura exata de cada comentário executável com versão ou sem ela (<c>/*!</c>,
    /// <c>/*!50000</c>, <c>/*M!100000</c>; <c>/*m!</c> é outra, porque o MariaDB só aceita o M maiúsculo).
    /// </param>
    public static ScanResult Scan(string text, bool backslashEscapes = true, bool executableComments = true, ICollection<string>? gates = null)
    {
        var statements = new List<MySqlStatement>();
        var all = new List<Token>();
        var current = new List<Token>();
        var delimiter = ";";
        var broken = false;
        var stmtBroken = false;
        var brokenFrom = -1;      // início de um trecho quebrado que ainda não tem token (ex.: "/* DROP ..." sozinho)
        var inExecutable = false; // dentro de /*! ... */ ou /*M! ... */
        var executableOpenedInStatement = false; // o comentário executável aberto começou depois do 1º token do statement
        var closedEnd = -1;       // fim do "*/" de um comentário executável fechado depois do último token (ver Flush)
        var i = 0;

        void Flush()
        {
            if (current.Count > 0 || stmtBroken)
            {
                var st = current.Count > 0 ? current[0].Start : brokenFrom;
                if (brokenFrom >= 0) st = Math.Min(st, brokenFrom);
                // Quebrado: o trecho vai até o fim do texto (o resto é string/comentário aberto).
                // Um statement que termina dentro de um comentário executável aberto nele mesmo leva o "*/" junto
                // (senão o servidor recebe o comentário sem fechar).
                var end = stmtBroken ? text.Length : Math.Max(current[^1].End, closedEnd);
                var isBroken = stmtBroken || !ParensBalanced(current) || (current.Count > 0 && current[0].Is("DELIMITER"));
                broken |= isBroken;
                statements.Add(new MySqlStatement(st, end, [.. current], isBroken));
            }
            current = [];
            stmtBroken = false;
            brokenFrom = -1;
            closedEnd = -1;
        }

        void MarkBroken(int at)
        {
            broken = stmtBroken = true;
            if (brokenFrom < 0) brokenFrom = at;
        }

        while (i < text.Length)
        {
            var c = text[i];

            if (IsSpace(c)) { i++; continue; }

            // DELIMITER xx numa linha própria, só entre statements.
            if (current.Count == 0 && !stmtBroken && !inExecutable && IsDelimiterLine(text, i, out var newDelimiter, out var lineEnd))
            {
                delimiter = newDelimiter;
                i = lineEnd;
                continue;
            }

            // Fim de um comentário executável: o "*/" some, o código de dentro já foi tokenizado.
            if (inExecutable && c == '*' && i + 1 < text.Length && text[i + 1] == '/')
            {
                inExecutable = false;
                i += 2;
                if (executableOpenedInStatement && current.Count > 0) closedEnd = i;
                continue;
            }

            // Comentários
            if (c == '#') { i = SkipLine(text, i); continue; }
            if (c == '-' && i + 1 < text.Length && text[i + 1] == '-' && (i + 2 >= text.Length || IsSpace(text[i + 2])))
            { i = SkipLine(text, i); continue; }
            if (c == '/' && i + 1 < text.Length && text[i + 1] == '*')
            {
                // /*! ... */ e /*M! ... */ (MariaDB) são código: pula só a abertura (e a versão) e segue tokenizando.
                // /*+ ... */ (dica de otimizador) é comentário comum.
                var open = executableComments ? ExecutableOpenerLength(text, i) : 0;
                if (open > 0 && !inExecutable)
                {
                    inExecutable = true;
                    executableOpenedInStatement = current.Count > 0;
                    var opener = i;
                    i += open;
                    while (i < text.Length && char.IsAsciiDigit(text[i])) i++;
                    gates?.Add(text[opener..i]);
                    continue;
                }
                var close = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                if (close < 0) { MarkBroken(i); i = text.Length; continue; }
                i = close + 2;
                continue;
            }

            // Terminador atual (pode ter mais de um caractere)
            if (string.CompareOrdinal(text, i, delimiter, 0, delimiter.Length) == 0)
            {
                all.Add(new Token(TokenKind.Terminator, i, delimiter.Length, delimiter));
                Flush();
                i += delimiter.Length;
                continue;
            }

            Token tok;
            if (c == '\'' || c == '"' || c == '`')
            {
                var end = ReadQuoted(text, i, c, backslashEscapes, out var closed);
                if (!closed) MarkBroken(i);
                tok = new Token(TokenKind.Quoted, i, end - i, text[i..end]);
                i = end;
            }
            else if (IsWordChar(c))
            {
                var j = i + 1;
                // Com um terminador customizado (ex.: $$), "END$$" é END seguido do terminador.
                while (j < text.Length && IsWordChar(text[j]) &&
                       (delimiter == ";" || string.CompareOrdinal(text, j, delimiter, 0, delimiter.Length) != 0)) j++;
                tok = new Token(TokenKind.Word, i, j - i, text[i..j]);
                i = j;
            }
            else
            {
                tok = new Token(TokenKind.Symbol, i, 1, c.ToString());
                i++;
            }

            current.Add(tok);
            all.Add(tok);
        }

        // Comentário executável sem fechar: o servidor recusaria, mas não dá para garantir o que é código.
        if (inExecutable) MarkBroken(current.Count > 0 ? current[0].Start : text.Length);
        Flush();
        return new ScanResult(all, statements, broken);
    }

    /// <summary>
    /// Espaço para o servidor: só espaço ASCII e caracteres de controle (o "--" vira comentário quando seguido de um deles).
    /// Espaços Unicode (U+00A0, U+2003, U+3000...) são caracteres de identificador, como qualquer caractere acima de 127.
    /// </summary>
    internal static bool IsSpace(char c) => c <= ' ' || c == (char)0x7f;

    private static bool IsWordChar(char c) => char.IsAsciiLetterOrDigit(c) || c == '_' || c == '$' || c == '@' || c > 127;

    private static int ExecutableOpenerLength(string t, int i)
    {
        if (i + 2 >= t.Length) return 0;
        if (t[i + 2] == '!') return 3;
        if ((t[i + 2] == 'M' || t[i + 2] == 'm') && i + 3 < t.Length && t[i + 3] == '!') return 4;
        return 0;
    }

    private static bool ParensBalanced(IReadOnlyList<Token> tokens)
    {
        var depth = 0;
        foreach (var t in tokens)
        {
            if (t.IsSymbol("(")) depth++;
            else if (t.IsSymbol(")") && --depth < 0) return false;
        }
        return depth == 0;
    }

    private static int SkipLine(string t, int i)
    {
        while (i < t.Length && t[i] != '\n' && t[i] != '\r') i++;
        return i;
    }

    private static int ReadQuoted(string t, int start, char quote, bool backslashEscapes, out bool closed)
    {
        var i = start + 1;
        while (i < t.Length)
        {
            if (backslashEscapes && t[i] == '\\' && quote != '`') { i += 2; continue; }
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

    /// <summary>Tira só o espaço ASCII/controle das pontas (string.Trim tiraria também os espaços Unicode).</summary>
    internal static string TrimSpace(string s)
    {
        int a = 0, b = s.Length;
        while (a < b && IsSpace(s[a])) a++;
        while (b > a && IsSpace(s[b - 1])) b--;
        return s[a..b];
    }

    private static bool IsDelimiterLine(string t, int i, out string delimiter, out int lineEnd)
    {
        delimiter = ";";
        lineEnd = i;
        const string kw = "DELIMITER";
        if (i + kw.Length >= t.Length || string.Compare(t, i, kw, 0, kw.Length, StringComparison.OrdinalIgnoreCase) != 0) return false;
        // Precisa estar no início da linha (só espaço antes) e ter espaço depois da palavra.
        var ls = i;
        while (ls > 0 && t[ls - 1] != '\n' && t[ls - 1] != '\r') { if (!IsSpace(t[ls - 1])) return false; ls--; }
        var j = i + kw.Length;
        if (j >= t.Length || (t[j] != ' ' && t[j] != '\t')) return false;
        while (j < t.Length && (t[j] == ' ' || t[j] == '\t')) j++;
        var s = j;
        while (j < t.Length && t[j] != '\n' && t[j] != '\r') j++;
        var d = TrimSpace(t[s..j]);
        // Terminador vazio, com espaço no meio ou com barra invertida: malformado. A linha fica como statement
        // (começando por DELIMITER), que é marcado como quebrado.
        if (d.Length == 0 || d.Any(IsSpace) || d.Contains('\\')) return false;
        delimiter = d;
        lineEnd = j;
        return true;
    }
}
