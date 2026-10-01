using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace SqlDesk.SqlAnalysis;

/// <summary>
/// Decide se um texto só lê dados. Usado antes de <b>reexecutar</b> um statement para exportar: reexecutar um SELECT é inofensivo,
/// reexecutar um INSERT, um EXEC ou um SELECT ... INTO repetiria o efeito. Tudo que não é reconhecido como leitura é recusado.
/// </summary>
public static class ReadOnlyAnalyzer
{
    public static bool IsReadOnly(string script, out string? reason)
    {
        reason = null;
        foreach (var batch in BatchSplitter.Split(script))
        {
            if (string.IsNullOrWhiteSpace(batch.Text)) continue;

            var parser = new TSql160Parser(initialQuotedIdentifiers: true);
            TSqlFragment? fragment;
            IList<ParseError> errors;
            using (var reader = new StringReader(batch.Text))
                fragment = parser.Parse(reader, out errors);
            if (errors.Count > 0 || fragment is null)
            {
                reason = "o texto não pôde ser analisado (erro de sintaxe)";
                return false;
            }

            var visitor = new ReadOnlyVisitor();
            fragment.Accept(visitor);
            if (visitor.Reason is not null)
            {
                reason = visitor.Reason;
                return false;
            }
        }
        return true;
    }

    private sealed class ReadOnlyVisitor : TSqlFragmentVisitor
    {
        public string? Reason { get; private set; }

        public override void Visit(TSqlStatement node)
        {
            if (Reason is not null) return;
            switch (node)
            {
                case SelectStatement s:
                    if (s.Into is not null) Reason = "contém SELECT ... INTO (cria uma tabela)";
                    return;
                // Variáveis e opções de sessão não alteram dados.
                case DeclareVariableStatement or SetVariableStatement or PredicateSetStatement or UseStatement or PrintStatement:
                    return;
                default:
                    Reason = $"contém {Describe(node)}, que não é só leitura";
                    return;
            }
        }

        private static string Describe(TSqlStatement node)
        {
            var name = node.GetType().Name;
            return name.EndsWith("Statement", StringComparison.Ordinal) ? name[..^"Statement".Length].ToUpperInvariant() : name;
        }
    }
}
