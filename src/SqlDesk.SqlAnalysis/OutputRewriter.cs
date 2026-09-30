using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace SqlDesk.SqlAnalysis;

/// <summary>
/// Reescreve UPDATE/DELETE bloqueados para devolver as linhas afetadas (<c>OUTPUT deleted.*, inserted.*</c> no
/// UPDATE, <c>OUTPUT deleted.*</c> no DELETE), usando a AST e o <c>Sql160ScriptGenerator</c>; nunca por regex.
/// O resultado alimenta o diálogo de "antes e depois" da segunda confirmação.
/// </summary>
/// <remarks>
/// Limitação do SQL Server: <c>OUTPUT</c> sem <c>INTO</c> falha (erro 334) em tabelas com trigger habilitado.
/// Quem executa deve tratar essa falha (desfazer e oferecer a execução sem pré-visualização).
/// </remarks>
public static class OutputRewriter
{
    public static RewriteResult Rewrite(string script)
    {
        var core = SqlScriptAnalyzer.AnalyzeCore(script, 0);
        var batches = core.Analysis.Batches.ToList();
        var rewritten = new List<RewrittenStatement>();
        var notRewritten = new List<DangerousStatement>();

        var generator = new Sql160ScriptGenerator(new SqlScriptGeneratorOptions
        {
            KeywordCasing = KeywordCasing.Uppercase,
            AlignClauseBodies = false,
            IncludeSemicolons = false,
            NewLineBeforeFromClause = false,
            NewLineBeforeWhereClause = false,
            NewLineBeforeOutputClause = false,
            NewLineBeforeJoinClause = false,
            NewLineBeforeGroupByClause = false,
            NewLineBeforeOrderByClause = false,
            NewLineBeforeHavingClause = false,
        });

        foreach (var hit in core.Hits)
        {
            if (!hit.Danger.CanPreviewWithOutput || hit.Node is null ||
                hit.Danger.Kind is not (DangerKind.UpdateWithoutWhere or DangerKind.DeleteWithoutWhere))
                notRewritten.Add(hit.Danger);
        }

        // Substitui de trás para frente dentro de cada batch, para não invalidar os offsets dos anteriores.
        foreach (var group in core.Hits
                     .Where(h => h.Danger.CanPreviewWithOutput && h.Node is not null
                                 && h.Danger.Kind is DangerKind.UpdateWithoutWhere or DangerKind.DeleteWithoutWhere)
                     .GroupBy(h => h.BatchIndex))
        {
            var batch = batches[group.Key];
            var text = batch.Text;
            foreach (var hit in group.OrderByDescending(h => h.Danger.Start))
            {
                var relStart = hit.Danger.Start - batch.Start;
                var original = text.Substring(relStart, hit.Danger.Length);
                var sql = Generate(generator, hit.Node!);
                if (original.TrimEnd().EndsWith(';')) sql += ";";
                text = text[..relStart] + sql + text[(relStart + hit.Danger.Length)..];
                rewritten.Add(new RewrittenStatement(hit.Danger.Kind, hit.Danger.Target, hit.Danger.Start, hit.Danger.Line, batch.Index));
            }
            batches[group.Key] = batch with { Text = text };
        }

        // Ordem de execução dentro do script: cada linha OUTPUT chega na ordem dos statements.
        rewritten.Sort((a, b) => a.OriginalStart.CompareTo(b.OriginalStart));
        return new RewriteResult(batches, rewritten, notRewritten);
    }

    private static string Generate(SqlScriptGenerator generator, TSqlStatement node)
    {
        switch (node)
        {
            case UpdateStatement u:
                u.UpdateSpecification.OutputClause = Output(includeInserted: true);
                break;
            case DeleteStatement d:
                d.DeleteSpecification.OutputClause = Output(includeInserted: false);
                break;
        }
        generator.GenerateScript(node, out var sql);
        return sql.Trim();
    }

    private static OutputClause Output(bool includeInserted)
    {
        var clause = new OutputClause();
        clause.SelectColumns.Add(Star("deleted"));
        if (includeInserted) clause.SelectColumns.Add(Star("inserted"));
        return clause;
    }

    private static SelectStarExpression Star(string qualifier)
    {
        var star = new SelectStarExpression { Qualifier = new MultiPartIdentifier() };
        star.Qualifier.Identifiers.Add(new Identifier { Value = qualifier });
        return star;
    }
}
