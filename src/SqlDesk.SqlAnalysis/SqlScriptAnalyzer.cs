using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace SqlDesk.SqlAnalysis;

public static class SqlScriptAnalyzer
{
    /// <summary>
    /// Analisa o script inteiro antes de qualquer comando chegar ao banco. Separa em batches por <c>GO</c>, faz o
    /// parse de cada um com o <c>TSql160Parser</c> e devolve os statements que exigem confirmação, os erros de
    /// sintaxe (com linha no documento) e avisos de SQL dinâmico que não dá para analisar.
    /// </summary>
    public static ScriptAnalysis Analyze(string script) => AnalyzeCore(script, 0).Analysis;

    internal sealed record CoreResult(ScriptAnalysis Analysis, List<Hit> Hits);

    internal static CoreResult AnalyzeCore(string script, int depth)
    {
        var batches = BatchSplitter.Split(script);
        var map = new LineMap(script);
        var hits = new List<Hit>();
        var syntaxErrors = new List<AnalysisDiagnostic>();
        var warnings = new List<string>();

        foreach (var batch in batches)
        {
            var parser = new TSql160Parser(initialQuotedIdentifiers: true);
            TSqlFragment? fragment;
            IList<ParseError> errors;
            using (var reader = new StringReader(batch.Text))
                fragment = parser.Parse(reader, out errors);

            if (errors.Count == 0 && fragment is not null)
            {
                hits.AddRange(DangerScanner.Scan(fragment, batch, script, map, depth, warnings));
                continue;
            }

            foreach (var e in errors)
                syntaxErrors.Add(new AnalysisDiagnostic(e.Message, batch.StartLine + e.Line - 1, e.Column));

            // Sem AST confiável, mas o lexer do ScriptDom ainda separa os tokens. Se o trecho contém
            // UPDATE/DELETE/TRUNCATE/DROP, não dá para garantir que é inofensivo: trata como perigoso.
            if (FindDestructiveKeyword(batch) is { } kw)
            {
                var start = batch.Start + kw.Offset;
                hits.Add(new Hit(new DangerousStatement(
                    DangerKind.Unanalyzable, start, Math.Max(1, batch.End - start), map.LineOf(start),
                    $"Não foi possível analisar este trecho (erro de sintaxe) e ele contém {kw.Keyword}; tratado como perigoso por segurança",
                    Target: null, CountQueries: [], CanPreviewWithOutput: false), null, batch.Index));
            }
        }

        var analysis = new ScriptAnalysis(batches, hits.Select(h => h.Danger).OrderBy(d => d.Start).ToList(), syntaxErrors, warnings);
        return new CoreResult(analysis, hits);
    }

    private static (string Keyword, int Offset)? FindDestructiveKeyword(Batch batch)
    {
        var parser = new TSql160Parser(initialQuotedIdentifiers: true);
        using var reader = new StringReader(batch.Text);
        var tokens = parser.GetTokenStream(reader, out _);
        foreach (var t in tokens)
        {
            var kw = t.TokenType switch
            {
                TSqlTokenType.Update => "UPDATE",
                TSqlTokenType.Delete => "DELETE",
                TSqlTokenType.Truncate => "TRUNCATE",
                TSqlTokenType.Drop => "DROP",
                _ => null,
            };
            if (kw is not null) return (kw, t.Offset);
        }
        return null;
    }
}
