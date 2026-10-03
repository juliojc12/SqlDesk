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
