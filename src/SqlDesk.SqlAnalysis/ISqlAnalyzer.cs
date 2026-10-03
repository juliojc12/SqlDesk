namespace SqlDesk.SqlAnalysis;

/// <summary>Análise de SQL específica de um banco. Quem executa pergunta ao analisador do provedor da aba.</summary>
public interface ISqlAnalyzer
{
    ScriptAnalysis Analyze(string script);

    LocateResult Locate(string text, int cursor);

    /// <summary>Reescreve UPDATE/DELETE perigosos para devolver as linhas afetadas. Sem esse recurso, devolve o texto como veio.</summary>
    RewriteResult Rewrite(string script);

    bool IsReadOnly(string script, out string? reason);

    /// <summary>
    /// O trecho tem comando que confirma a transação sozinho no servidor (DDL, START TRANSACTION/BEGIN/COMMIT, LOCK...)
    /// ou que não dá para enxergar por dentro (CALL, EXECUTE, bloco de código, texto quebrado). Só faz sentido em bancos
    /// que confirmam DDL sozinhos; nos outros, nunca.
    /// </summary>
    bool CausesImplicitCommit(string batchText) => false;
}
