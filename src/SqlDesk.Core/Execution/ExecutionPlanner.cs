using SqlDesk.SqlAnalysis;

namespace SqlDesk.Core.Execution;

public sealed record BlockedInfo(int Line, string Description);

public abstract record ExecutionPlan
{
    /// <summary>Nada a executar (cursor numa linha em branco, texto vazio...). Nada vai ao banco.</summary>
    public sealed record Nothing(string Message) : ExecutionPlan;

    /// <summary>A análise de segurança barrou o texto. Nada vai ao banco.</summary>
    public sealed record Refused(string Message, IReadOnlyList<BlockedInfo> Blocked) : ExecutionPlan;

    /// <param name="BaseOffset">Offset, no documento, onde o texto analisado começa.</param>
    /// <param name="BaseLine">Linha (base 1), no documento, onde o texto analisado começa.</param>
    /// <param name="Highlight">Trecho a destacar no editor (statement achado sob o cursor); nulo para seleção e script.</param>
    public sealed record Runnable(
        IReadOnlyList<Batch> Batches, int BaseOffset, int BaseLine, DocRange? Highlight, IReadOnlyList<string> Warnings) : ExecutionPlan;
}

/// <summary>
/// Decide o que executar (seleção, statement sob o cursor ou o script inteiro) e submete o texto à análise de
/// segurança antes de qualquer comando chegar ao banco. O frontend só informa o texto, o cursor e a seleção.
/// </summary>
public static class ExecutionPlanner
{
    public static ExecutionPlan Plan(string text, int cursor, int selectionStart, int selectionEnd, bool wholeScript)
    {
        var start = 0;
        var length = text.Length;
        DocRange? highlight = null;

        if (!wholeScript)
        {
            var (s, e) = (Math.Clamp(Math.Min(selectionStart, selectionEnd), 0, text.Length), Math.Clamp(Math.Max(selectionStart, selectionEnd), 0, text.Length));
            if (e > s)
            {
                (start, length) = (s, e - s);
            }
            else
            {
                var located = StatementLocator.Locate(text, Math.Clamp(cursor, 0, text.Length));
                if (located.Range is not { } range)
                    return new ExecutionPlan.Nothing(located.Message ?? "Nenhum statement sob o cursor.");
                (start, length) = (range.Start, range.Length);
                highlight = new DocRange(range.Start, range.Length);
            }
        }

        var sub = text.Substring(start, length);
        if (string.IsNullOrWhiteSpace(sub)) return new ExecutionPlan.Nothing("Não há texto para executar.");

        var baseLine = 1 + text.AsSpan(0, start).Count('\n');
        var analysis = SqlScriptAnalyzer.Analyze(sub);

        if (!analysis.IsSafe)
        {
            var blocked = analysis.Dangers.Select(d => new BlockedInfo(baseLine - 1 + d.Line, d.Description)).ToList();
            return new ExecutionPlan.Refused(
                "Execução bloqueada: o texto contém comandos que exigem confirmação (UPDATE/DELETE sem WHERE, TRUNCATE ou DROP). " +
                "O fluxo de dupla confirmação com transação ainda não existe; por segurança, nada foi enviado ao banco.",
                blocked);
        }

        // Repetir um batch (GO n) é arriscado para qualquer escrita; por segurança, não é executado.
        if (analysis.Batches.FirstOrDefault(b => b.RepeatCount > 1) is { } repeated)
        {
            return new ExecutionPlan.Refused(
                $"GO {repeated.RepeatCount} (repetir o batch) ainda não é suportado; nada foi enviado ao banco.",
                [new BlockedInfo(baseLine - 1 + repeated.StartLine, $"GO {repeated.RepeatCount}")]);
        }

        return new ExecutionPlan.Runnable(analysis.Batches, start, baseLine, highlight, analysis.Warnings);
    }
}
