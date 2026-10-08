using System.Text.RegularExpressions;

namespace SqlDesk.SqlAnalysis;

/// <summary>
/// Trava do SQL devolvido pela IA, independente do modelo: só passa UMA consulta que começa com SELECT ou WITH e que o
/// analisador do provedor reconhece como somente leitura. É deliberadamente conservadora (olha o texto cru, inclusive
/// strings e comentários): o custo de um falso positivo é só o SQL voltar comentado para o usuário revisar.
/// </summary>
public static partial class AiSqlGuard
{
    private static readonly string[] BlockedWords =
    [
        "OPENROWSET", "OPENQUERY", "OPENDATASOURCE", "OPENXML", "EXEC", "EXECUTE", "WAITFOR", "SLEEP", "BENCHMARK",
        "LOAD_FILE", "GET_LOCK", "OUTFILE", "DUMPFILE", "INTO", "DECLARE", "SHUTDOWN",
    ];

    public static bool Check(ISqlAnalyzer analyzer, string? sql, out string? reason)
    {
        reason = null;
        if (string.IsNullOrWhiteSpace(sql)) { reason = "a IA não devolveu nenhum SQL"; return false; }

        if (sql.Contains("/*!") || sql.Contains("/*M!", StringComparison.OrdinalIgnoreCase))
        {
            reason = "contém comentário executável do MySQL";
            return false;
        }

        var body = sql.Trim().TrimEnd(';', ' ', '\t', '\r', '\n');
        if (body.Contains(';')) { reason = "tem mais de um comando"; return false; }

        if (!StartsWithSelect(body)) { reason = "não começa com SELECT ou WITH"; return false; }

        foreach (var word in BlockedWords)
            if (Regex.IsMatch(body, $@"\b{word}\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            { reason = $"usa {word}, que pode ter efeito fora da leitura"; return false; }
        if (SystemProcedure().IsMatch(body)) { reason = "chama procedimento de sistema (xp_/sp_)"; return false; }
        if (LockingRead().IsMatch(body)) { reason = "usa bloqueio de leitura (FOR UPDATE / LOCK IN SHARE MODE)"; return false; }

        if (!analyzer.IsReadOnly(sql, out var why)) { reason = why ?? "não é somente leitura"; return false; }
        return true;
    }

    /// <summary>Pula espaços e comentários (<c>--</c>, <c>#</c>, <c>/* */</c>) e confere se o primeiro comando é SELECT ou WITH.</summary>
    private static bool StartsWithSelect(string text)
    {
        var i = 0;
        while (i < text.Length)
        {
            if (char.IsWhiteSpace(text[i])) i++;
            else if (text[i] == '#' || (text[i] == '-' && i + 1 < text.Length && text[i + 1] == '-'))
            {
                while (i < text.Length && text[i] != '\n') i++;
            }
            else if (text[i] == '/' && i + 1 < text.Length && text[i + 1] == '*')
            {
                var end = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                if (end < 0) return false;
                i = end + 2;
            }
            else break;
        }
        var rest = text.AsSpan(i);
        return StartsWithWord(rest, "SELECT") || StartsWithWord(rest, "WITH");
    }

    private static bool StartsWithWord(ReadOnlySpan<char> s, string word) =>
        s.Length > word.Length && s.StartsWith(word, StringComparison.OrdinalIgnoreCase) && !(char.IsLetterOrDigit(s[word.Length]) || s[word.Length] == '_');

    [GeneratedRegex(@"\b(xp|sp)_\w+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SystemProcedure();

    [GeneratedRegex(@"\bFOR\s+UPDATE\b|\bLOCK\s+IN\s+SHARE\s+MODE\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LockingRead();
}
