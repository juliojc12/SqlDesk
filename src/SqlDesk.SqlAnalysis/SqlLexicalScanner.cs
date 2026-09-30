namespace SqlDesk.SqlAnalysis;

/// <summary>Uma linha do script. <see cref="End"/> exclui a quebra de linha; <see cref="NextStart"/> é o início da próxima.</summary>
internal readonly record struct ScannedLine(int Start, int End, int NextStart, bool StartsInCode, bool IsGo, int GoCount);

/// <summary>
/// Varredura léxica mínima de T-SQL: sabe o que é texto "de código" e o que está em string, identificador
/// entre colchetes/aspas ou comentário (inclusive comentários de bloco aninhados). É o que permite achar
/// <c>GO</c> e <c>;</c> sem se enganar com o conteúdo de strings e comentários.
/// </summary>
internal sealed class SqlLexicalScanner
{
    private enum State { Code, LineComment, BlockComment, SingleQuote, DoubleQuote, Bracket }

    public string Text { get; }

    public IReadOnlyList<ScannedLine> Lines => _lines;

    /// <summary>true = o caractere está em código (fora de strings, identificadores delimitados e comentários).</summary>
    public bool IsCode(int offset) => _code[offset];

    private readonly List<ScannedLine> _lines = [];
    private readonly bool[] _code;

    public SqlLexicalScanner(string text)
    {
        Text = text;
        _code = new bool[text.Length];
        Scan();
    }

    private void Scan()
    {
        var t = Text;
        var state = State.Code;
        var depth = 0;
        var lineStart = 0;
        var startsInCode = true;

        for (var i = 0; i <= t.Length; i++)
        {
            var atEnd = i == t.Length;
            var c = atEnd ? '\0' : t[i];

            if (atEnd || c == '\n' || c == '\r')
            {
                var next = i;
                if (!atEnd)
                {
                    next = c == '\r' && i + 1 < t.Length && t[i + 1] == '\n' ? i + 2 : i + 1;
                }

                var isGo = false;
                var goCount = 1;
                if (startsInCode) isGo = TryParseGo(t, lineStart, i, out goCount);
                _lines.Add(new ScannedLine(lineStart, i, next, startsInCode, isGo, goCount));

                // Comentário de linha termina na quebra de linha.
                if (state == State.LineComment) state = State.Code;
                lineStart = next;
                startsInCode = state == State.Code;
                if (atEnd) break;
                i = next - 1;
                continue;
            }

            switch (state)
            {
                case State.Code:
                    if (c == '-' && Peek(i + 1) == '-') { state = State.LineComment; i++; }
                    else if (c == '/' && Peek(i + 1) == '*') { state = State.BlockComment; depth = 1; i++; }
                    else if (c == '\'') state = State.SingleQuote;
                    else if (c == '"') state = State.DoubleQuote;
                    else if (c == '[') state = State.Bracket;
                    else _code[i] = true;
                    break;
                case State.LineComment:
                    break;
                case State.BlockComment:
                    if (c == '/' && Peek(i + 1) == '*') { depth++; i++; }
                    else if (c == '*' && Peek(i + 1) == '/')
                    {
                        i++;
                        if (--depth == 0) state = State.Code;
                    }
                    break;
                case State.SingleQuote:
                    if (c == '\'') { if (Peek(i + 1) == '\'') i++; else state = State.Code; }
                    break;
                case State.DoubleQuote:
                    if (c == '"') { if (Peek(i + 1) == '"') i++; else state = State.Code; }
                    break;
                case State.Bracket:
                    if (c == ']') { if (Peek(i + 1) == ']') i++; else state = State.Code; }
                    break;
            }
        }
    }

    private char Peek(int i) => i < Text.Length ? Text[i] : '\0';

    /// <summary>
    /// Linha que é um separador de batch: <c>GO</c> sozinho, opcionalmente seguido de uma contagem e/ou de um
    /// comentário (<c>-- ...</c> ou <c>/* ... */</c> fechado na mesma linha).
    /// </summary>
    private static bool TryParseGo(string t, int start, int end, out int count)
    {
        count = 1;
        var i = start;
        while (i < end && (t[i] == ' ' || t[i] == '\t')) i++;
        if (i + 2 > end) return false;
        if (char.ToUpperInvariant(t[i]) != 'G' || char.ToUpperInvariant(t[i + 1]) != 'O') return false;
        i += 2;
        // "GO" não pode ser prefixo de outro identificador (GOTO, GOAL...).
        if (i < end && (char.IsLetterOrDigit(t[i]) || t[i] == '_' || t[i] == '@' || t[i] == '#' || t[i] == '$')) return false;

        while (i < end && (t[i] == ' ' || t[i] == '\t')) i++;
        if (i < end && char.IsDigit(t[i]))
        {
            var numStart = i;
            while (i < end && char.IsDigit(t[i])) i++;
            if (!int.TryParse(t.AsSpan(numStart, i - numStart), out count) || count < 1) return false;
            while (i < end && (t[i] == ' ' || t[i] == '\t')) i++;
        }

        while (i < end)
        {
            if (t[i] == '-' && i + 1 < end && t[i + 1] == '-') return true; // resto da linha é comentário
            if (t[i] == '/' && i + 1 < end && t[i + 1] == '*')
            {
                var close = t.IndexOf("*/", i + 2, end - (i + 2), StringComparison.Ordinal);
                if (close < 0) return false;
                i = close + 2;
                while (i < end && (t[i] == ' ' || t[i] == '\t')) i++;
                continue;
            }
            return false; // qualquer outra coisa na linha: não é GO
        }
        return true;
    }
}

/// <summary>Conversão offset ↔ linha/coluna (base 1).</summary>
public sealed class LineMap
{
    private readonly int[] _starts;

    public LineMap(string text)
    {
        var starts = new List<int> { 0 };
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n') starts.Add(i + 1);
            else if (text[i] == '\r')
            {
                if (i + 1 < text.Length && text[i + 1] == '\n') i++;
                starts.Add(i + 1);
            }
        }
        _starts = [.. starts];
    }

    public int LineCount => _starts.Length;

    public int LineStart(int line) => _starts[line - 1];

    /// <summary>Linha (base 1) que contém o offset.</summary>
    public int LineOf(int offset)
    {
        var idx = Array.BinarySearch(_starts, offset);
        return (idx >= 0 ? idx : ~idx - 1) + 1;
    }

    public int ColumnOf(int offset) => offset - _starts[LineOf(offset) - 1] + 1;
}
