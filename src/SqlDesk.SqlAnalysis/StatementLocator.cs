using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace SqlDesk.SqlAnalysis;

public static class StatementLocator
{
    private const string BlankLineMessage = "O cursor está numa linha em branco; posicione-o sobre um statement.";
    private const string NothingMessage = "Não há nenhum statement sob o cursor.";

    /// <summary>
    /// Acha o statement que o Ctrl+Enter deve executar (modelo do DBeaver). Faz o parse só do batch do cursor
    /// (o <c>GO</c> não é T-SQL) e escolhe entre os statements de nível superior. Se o parse falhar, usa o bloco ao
    /// redor do cursor delimitado por linhas em branco ou por <c>;</c>.
    /// </summary>
    public static LocateResult Locate(string text, int cursor)
    {
        cursor = Math.Clamp(cursor, 0, text.Length);
        var scanner = new SqlLexicalScanner(text);
        var map = new LineMap(text);

        var cursorLine = scanner.Lines.FirstOrDefault(l => cursor >= l.Start && cursor <= l.End);
        if (cursorLine.IsGo) return new LocateResult(null, "O cursor está numa linha GO (separador de batch).", false);

        var batch = BatchSplitter.Split(text).FirstOrDefault(b => cursor >= b.Start && cursor <= b.End);
        if (batch is null) return new LocateResult(null, IsBlank(text, cursorLine) ? BlankLineMessage : NothingMessage, false);

        var parser = new TSql160Parser(initialQuotedIdentifiers: true);
        TSqlFragment? fragment;
        IList<ParseError> errors;
        using (var reader = new StringReader(batch.Text))
            fragment = parser.Parse(reader, out errors);

        if (errors.Count > 0 || fragment is not TSqlScript script)
            return Fallback(text, scanner, batch, cursor, cursorLine);

        var ranges = script.Batches
            .SelectMany(b => b.Statements)
            .Select(s => new TextRange(batch.Start + s.StartOffset, s.FragmentLength))
            .ToList();

        var chosen = Choose(text, scanner, ranges, cursor, cursorLine) ?? FromCommentAbove(text, scanner, ranges, cursorLine);
        if (chosen is not null) return new LocateResult(chosen, null, false);
        return new LocateResult(null, IsBlank(text, cursorLine) ? BlankLineMessage : NothingMessage, false);
    }

    private static TextRange? Choose(string text, SqlLexicalScanner scanner, List<TextRange> ranges, int c, ScannedLine cursorLine)
    {
        // Cursor logo depois do último caractere de um statement ("SELECT 1|"): é esse.
        var justAfter = ranges.LastOrDefault(r => r.End == c);
        if (justAfter is not null) return justAfter;

        // Cursor dentro do statement.
        var inside = ranges.FirstOrDefault(r => c >= r.Start && c < r.End);
        if (inside is not null) return inside;

        // Entre statements, na mesma linha do fim de um deles ("SELECT 1;   |" ou "SELECT 1 -- nota|").
        var sameLineEnd = ranges.LastOrDefault(r => r.End <= c && r.End > cursorLine.Start && r.End - 1 <= cursorLine.End);
        if (sameLineEnd is not null) return sameLineEnd;

        // Cursor na indentação antes de um statement que começa nesta linha.
        var startsHere = ranges.FirstOrDefault(r => r.Start >= c && r.Start <= cursorLine.End
            && text.AsSpan(c, r.Start - c).IsWhiteSpace());
        return startsHere;
    }

    /// <summary>
    /// Cursor numa linha só de comentário colada em cima de um statement (sem linha em branco no meio): é o statement de baixo.
    /// </summary>
    private static TextRange? FromCommentAbove(string text, SqlLexicalScanner scanner, List<TextRange> ranges, ScannedLine cursorLine)
    {
        if (IsBlank(text, cursorLine) || HasCode(scanner, cursorLine)) return null;
        var lines = scanner.Lines;
        var i = lines.ToList().IndexOf(cursorLine);
        for (i++; i < lines.Count; i++)
        {
            var l = lines[i];
            if (l.StartsInCode && IsBlank(text, l)) return null;
            if (HasCode(scanner, l)) return ranges.FirstOrDefault(r => r.Start >= l.Start && r.Start <= l.End);
        }
        return null;
    }

    private static bool HasCode(SqlLexicalScanner scanner, ScannedLine line)
    {
        for (var i = line.Start; i < line.End; i++)
            if (scanner.IsCode(i) && !char.IsWhiteSpace(scanner.Text[i])) return true;
        return false;
    }

    private static bool IsBlank(string text, ScannedLine line) => text.AsSpan(line.Start, line.End - line.Start).IsWhiteSpace();

    private static LocateResult Fallback(string text, SqlLexicalScanner scanner, Batch batch, int c, ScannedLine cursorLine)
    {
        if (IsBlank(text, cursorLine)) return new LocateResult(null, BlankLineMessage, true);

        // Limites: início do batch / linhas em branco / ';' em código.
        var start = batch.Start;
        var end = batch.End;

        foreach (var l in scanner.Lines)
        {
            if (!l.StartsInCode || !IsBlank(text, l) || l.End < batch.Start || l.Start > batch.End) continue;
            if (l.NextStart <= cursorLine.Start) start = Math.Max(start, l.NextStart);
            else if (l.Start >= cursorLine.NextStart && l.Start < end) end = Math.Min(end, l.Start);
        }

        // Cursor logo depois de um ';' pertence ao statement anterior.
        var before = c > 0 && c - 1 >= batch.Start && scanner.IsCode(c - 1) && text[c - 1] == ';' ? c - 1 : c;
        for (var i = before - 1; i >= start; i--)
            if (scanner.IsCode(i) && text[i] == ';') { start = i + 1; break; }
        for (var i = before; i < end; i++)
            if (scanner.IsCode(i) && text[i] == ';') { end = i + 1; break; }

        while (start < end && char.IsWhiteSpace(text[start])) start++;
        while (end > start && char.IsWhiteSpace(text[end - 1])) end--;

        return end > start
            ? new LocateResult(new TextRange(start, end - start), null, true)
            : new LocateResult(null, NothingMessage, true);
    }
}
