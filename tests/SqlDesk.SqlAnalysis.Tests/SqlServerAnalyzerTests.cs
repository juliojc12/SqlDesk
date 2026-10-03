using SqlDesk.SqlAnalysis;

namespace SqlDesk.SqlAnalysis.Tests;

public class SqlServerAnalyzerTests
{
    private static readonly ISqlAnalyzer A = SqlServerAnalyzer.Instance;

    [Fact]
    public void Delega_para_o_analisador_atual()
    {
        Assert.False(A.Analyze("DELETE FROM t").IsSafe);
        Assert.True(A.Analyze("SELECT 1").IsSafe);
        Assert.True(A.IsReadOnly("SELECT 1", out _));
        Assert.False(A.IsReadOnly("DELETE FROM t", out var reason));
        Assert.NotNull(reason);
        Assert.True(A.Locate("SELECT 1", 3).Found);
        Assert.Single(A.Rewrite("DELETE FROM t").Rewritten);
    }
}
