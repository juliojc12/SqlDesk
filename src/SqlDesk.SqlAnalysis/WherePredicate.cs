using System.Globalization;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace SqlDesk.SqlAnalysis;

internal enum WhereKind
{
    /// <summary>Sem cláusula WHERE.</summary>
    Missing,

    /// <summary>Há WHERE, mas ele não restringe nada (não referencia coluna ou é sempre verdadeiro).</summary>
    NoEffect,

    Restrictive,
}

/// <summary>Decide se um WHERE de fato filtra linhas.</summary>
internal static class WherePredicate
{
    public static WhereKind Classify(WhereClause? where)
    {
        if (where is null) return WhereKind.Missing;
        if (where.Cursor is not null) return WhereKind.Restrictive; // WHERE CURRENT OF: posicionado num cursor
        if (where.SearchCondition is null) return WhereKind.Missing;

        var cond = where.SearchCondition;
        if (!ReferencesColumn(cond)) return WhereKind.NoEffect;   // regra da especificação: 1=1, 'a'='a', 1=0...
        if (Evaluate(cond) == Truth.True) return WhereKind.NoEffect; // 1=1 OR id = 5, id = id...
        return WhereKind.Restrictive;
    }

    private sealed class ColumnFinder : TSqlFragmentVisitor
    {
        public bool Found { get; private set; }

        public override void Visit(ColumnReferenceExpression node) => Found = true;
    }

    private static bool ReferencesColumn(TSqlFragment fragment)
    {
        var f = new ColumnFinder();
        fragment.Accept(f);
        return f.Found;
    }

    private enum Truth { True, False, Unknown }

    private static Truth Evaluate(BooleanExpression e)
    {
        switch (e)
        {
            case BooleanParenthesisExpression p:
                return Evaluate(p.Expression);

            case BooleanNotExpression n:
                return Evaluate(n.Expression) switch { Truth.True => Truth.False, Truth.False => Truth.True, _ => Truth.Unknown };

            case BooleanBinaryExpression b:
            {
                var l = Evaluate(b.FirstExpression);
                var r = Evaluate(b.SecondExpression);
                if (b.BinaryExpressionType == BooleanBinaryExpressionType.Or)
                {
                    if (l == Truth.True || r == Truth.True) return Truth.True;
                    return l == Truth.False && r == Truth.False ? Truth.False : Truth.Unknown;
                }
                if (l == Truth.False || r == Truth.False) return Truth.False;
                return l == Truth.True && r == Truth.True ? Truth.True : Truth.Unknown;
            }

            case BooleanComparisonExpression c:
                return EvaluateComparison(c);

            default:
                return Truth.Unknown;
        }
    }

    private static Truth EvaluateComparison(BooleanComparisonExpression c)
    {
        var type = c.ComparisonType;

        // coluna comparada com ela mesma (x = x, x >= x, x <= x): não filtra (ignora a semântica de NULL, o que erra para o lado seguro)
        if (Unwrap(c.FirstExpression) is ColumnReferenceExpression a && Unwrap(c.SecondExpression) is ColumnReferenceExpression b
            && SameColumn(a, b))
        {
            return type is BooleanComparisonType.Equals or BooleanComparisonType.GreaterThanOrEqualTo or BooleanComparisonType.LessThanOrEqualTo
                ? Truth.True
                : Truth.Unknown;
        }

        if (!TryLiteral(c.FirstExpression, out var x) || !TryLiteral(c.SecondExpression, out var y)) return Truth.Unknown;

        int cmp;
        switch (x, y)
        {
            case (decimal dx, decimal dy):
                cmp = dx.CompareTo(dy);
                break;
            case (string sx, string sy):
                sx = sx.TrimEnd(' ');
                sy = sy.TrimEnd(' ');
                var ignoreCase = string.Compare(sx, sy, StringComparison.OrdinalIgnoreCase);
                var ordinal = string.CompareOrdinal(sx, sy);
                // Iguais só pela caixa: depende da collation do banco; não arrisca uma resposta.
                if (ignoreCase == 0 && ordinal != 0) return Truth.Unknown;
                cmp = ordinal;
                break;
            default:
                return Truth.Unknown;
        }

        return type switch
        {
            BooleanComparisonType.Equals => Bool(cmp == 0),
            BooleanComparisonType.NotEqualToBrackets or BooleanComparisonType.NotEqualToExclamation => Bool(cmp != 0),
            BooleanComparisonType.GreaterThan => Bool(cmp > 0),
            BooleanComparisonType.LessThan => Bool(cmp < 0),
            BooleanComparisonType.GreaterThanOrEqualTo => Bool(cmp >= 0),
            BooleanComparisonType.LessThanOrEqualTo => Bool(cmp <= 0),
            _ => Truth.Unknown,
        };

        static Truth Bool(bool v) => v ? Truth.True : Truth.False;
    }

    private static ScalarExpression Unwrap(ScalarExpression e) => e is ParenthesisExpression p ? Unwrap(p.Expression) : e;

    private static bool SameColumn(ColumnReferenceExpression a, ColumnReferenceExpression b)
    {
        var x = a.MultiPartIdentifier?.Identifiers;
        var y = b.MultiPartIdentifier?.Identifiers;
        if (x is null || y is null || x.Count != y.Count || x.Count == 0) return false;
        return x.Zip(y).All(p => string.Equals(p.First.Value, p.Second.Value, StringComparison.OrdinalIgnoreCase));
    }

    private static bool TryLiteral(ScalarExpression e, out object value)
    {
        value = null!;
        e = Unwrap(e);
        switch (e)
        {
            case IntegerLiteral or NumericLiteral or RealLiteral:
                if (decimal.TryParse(((Literal)e).Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) { value = d; return true; }
                return false;
            case StringLiteral s:
                value = s.Value ?? "";
                return true;
            case UnaryExpression { UnaryExpressionType: UnaryExpressionType.Negative } u when TryLiteral(u.Expression, out var inner) && inner is decimal di:
                value = -di;
                return true;
            case UnaryExpression { UnaryExpressionType: UnaryExpressionType.Positive } u2:
                return TryLiteral(u2.Expression, out value);
            default:
                return false;
        }
    }
}
