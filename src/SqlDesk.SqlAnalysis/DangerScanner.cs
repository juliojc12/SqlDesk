using System.Text;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace SqlDesk.SqlAnalysis;

/// <summary>Um perigo encontrado, com o nó da AST quando for possível reescrevê-lo (para o OUTPUT).</summary>
internal sealed record Hit(DangerousStatement Danger, TSqlStatement? Node, int BatchIndex);

/// <summary>
/// Percorre a AST do ScriptDom (nunca regex) e classifica os statements destrutivos:
/// UPDATE/DELETE sem filtro, TRUNCATE, DROP e ALTER TABLE ... DROP COLUMN.
/// O visitor alcança statements dentro de CTEs, BEGIN...END, IF, WHILE e procedures criadas no próprio script.
/// </summary>
internal sealed class DangerScanner : TSqlFragmentVisitor
{
    private const int MaxDynamicDepth = 3;

    private readonly Batch _batch;
    private readonly string _document;
    private readonly LineMap _map;
    private readonly int _depth;
    private readonly List<Hit> _hits = [];
    private readonly List<string> _warnings;
    private bool _sawWrite;

    private DangerScanner(Batch batch, string document, LineMap map, int depth, List<string> warnings)
    {
        _batch = batch;
        _document = document;
        _map = map;
        _depth = depth;
        _warnings = warnings;
    }

    /// <param name="writes">Há UPDATE/DELETE/MERGE (inclusive os com WHERE que filtra): base da recomendação de TRANSACTION.</param>
    public static List<Hit> Scan(TSqlFragment fragment, Batch batch, string document, LineMap map, int depth, List<string> warnings, out bool writes)
    {
        var scanner = new DangerScanner(batch, document, map, depth, warnings);
        fragment.Accept(scanner);
        writes = scanner._sawWrite;
        return scanner._hits;
    }

    // ---------- UPDATE / DELETE ----------

    public override void Visit(UpdateStatement node)
    {
        var spec = node.UpdateSpecification;
        _sawWrite = true;
        var kind = WherePredicate.Classify(spec.WhereClause);
        if (kind == WhereKind.Restrictive) return;

        var target = ResolveTarget(spec.Target, spec.FromClause);
        Add(node, DangerKind.UpdateWithoutWhere, target,
            $"UPDATE em {target} {Reason(kind)} vai afetar a tabela inteira",
            previewable: spec.OutputClause is null && spec.OutputIntoClause is null);
    }

    public override void Visit(DeleteStatement node)
    {
        var spec = node.DeleteSpecification;
        _sawWrite = true;
        var kind = WherePredicate.Classify(spec.WhereClause);
        if (kind == WhereKind.Restrictive) return;

        var target = ResolveTarget(spec.Target, spec.FromClause);
        Add(node, DangerKind.DeleteWithoutWhere, target,
            $"DELETE em {target} {Reason(kind)} vai apagar todas as linhas da tabela",
            previewable: spec.OutputClause is null && spec.OutputIntoClause is null);
    }

    public override void Visit(MergeStatement node) => _sawWrite = true;

    private static string Reason(WhereKind kind) =>
        kind == WhereKind.Missing ? "sem WHERE" : "com WHERE que não filtra nenhuma linha (não referencia colunas ou é sempre verdadeiro)";

    // ---------- TRUNCATE / DROP ----------

    public override void Visit(TruncateTableStatement node)
    {
        var name = Display(node.TableName);
        Add(node, DangerKind.TruncateTable, name,
            $"TRUNCATE TABLE em {name} remove todas as linhas da tabela",
            previewable: false, countQueries: [CountQuery(node.TableName)]);
    }

    /// <summary>Qualquer statement <c>Drop*</c> (os da especificação e também TRIGGER, SYNONYM, TYPE...): o mais conservador.</summary>
    public override void Visit(TSqlStatement node)
    {
        var type = node.GetType().Name;

        if (node is AlterTableDropTableElementStatement alter)
        {
            // Gramática: DROP { [CONSTRAINT] nome | COLUMN nome } [, ...]. Itens sem palavra-chave própria herdam
            // o tipo do item anterior ("DROP COLUMN c1, c2" apaga as duas colunas).
            var columns = new List<string>();
            var current = TableElementType.NotSpecified;
            foreach (var el in alter.AlterTableDropTableElements)
            {
                if (el.TableElementType != TableElementType.NotSpecified) current = el.TableElementType;
                if (current == TableElementType.Column) columns.Add(el.Name.Value);
            }
            if (columns.Count > 0)
            {
                var table = Display(alter.SchemaObjectName);
                Add(node, DangerKind.DropColumn, table,
                    $"ALTER TABLE {table} DROP COLUMN {string.Join(", ", columns)} apaga a(s) coluna(s) e os dados nela(s)", previewable: false);
            }
            return;
        }

        if (!type.StartsWith("Drop", StringComparison.Ordinal) || !type.EndsWith("Statement", StringComparison.Ordinal)) return;

        var (kind, names, counts) = DescribeDrop(node);
        var list = names.Count > 0 ? string.Join(", ", names) : "";
        var desc = names.Count > 0
            ? $"DROP {kind.Name} {list} remove {kind.Article} do banco de dados"
            : $"DROP {kind.Name} remove {kind.Article} do banco de dados";
        Add(node, DangerKind.Drop, names.FirstOrDefault(), desc, previewable: false, countQueries: counts);
    }

    private sealed record DropKind(string Name, string Article);

    private static (DropKind Kind, List<string> Names, List<string> Counts) DescribeDrop(TSqlStatement node)
    {
        var names = new List<string>();
        var counts = new List<string>();
        DropKind kind;

        switch (node)
        {
            case DropTableStatement t:
                kind = new("TABLE", "a tabela");
                foreach (var o in t.Objects) { names.Add(Display(o)); counts.Add(CountQuery(o)); }
                break;
            case DropViewStatement v:
                kind = new("VIEW", "a view");
                names.AddRange(v.Objects.Select(Display));
                break;
            case DropProcedureStatement p:
                kind = new("PROCEDURE", "a procedure");
                names.AddRange(p.Objects.Select(Display));
                break;
            case DropFunctionStatement f:
                kind = new("FUNCTION", "a função");
                names.AddRange(f.Objects.Select(Display));
                break;
            case DropSchemaStatement s:
                kind = new("SCHEMA", "o schema");
                names.Add(Display(s.Schema));
                break;
            case DropDatabaseStatement d:
                kind = new("DATABASE", "o banco de dados");
                names.AddRange(d.Databases.Select(i => i.Value));
                break;
            case DropIndexStatement ix:
                kind = new("INDEX", "o índice");
                foreach (var c in ix.DropIndexClauses)
                {
                    switch (c)
                    {
                        case DropIndexClause dc:
                            names.Add(dc.Object is null ? dc.Index.Value : $"{dc.Index.Value} ON {Display(dc.Object)}");
                            break;
                        case BackwardsCompatibleDropIndexClause bc:
                            names.Add(bc.Index is { } ci ? string.Join(".", ci.Identifiers.Select(i => i.Value)) : "?");
                            break;
                    }
                }
                break;
            case DropObjectsStatement other: // TRIGGER, SYNONYM, SEQUENCE, TYPE...
                kind = new(Humanize(node.GetType().Name), "o objeto");
                names.AddRange(other.Objects.Select(Display));
                break;
            default:
                kind = new(Humanize(node.GetType().Name), "o objeto");
                break;
        }
        return (kind, names, counts);
    }

    /// <summary>"DropTriggerStatement" vira "TRIGGER"; "DropExternalTableStatement" vira "EXTERNAL TABLE".</summary>
    private static string Humanize(string typeName)
    {
        var core = typeName["Drop".Length..^"Statement".Length];
        var sb = new StringBuilder();
        for (var i = 0; i < core.Length; i++)
        {
            if (i > 0 && char.IsUpper(core[i])) sb.Append(' ');
            sb.Append(char.ToUpperInvariant(core[i]));
        }
        return sb.ToString();
    }

    // ---------- SQL dinâmico ----------

    public override void Visit(ExecuteStatement node)
    {
        var entity = node.ExecuteSpecification?.ExecutableEntity;
        if (entity is ExecutableStringList list)
        {
            // EXEC('...') com literais: analisa o SQL dentro da string (senão seria um atalho para burlar as travas).
            var parts = list.Strings.Select(s => s as StringLiteral).ToList();
            if (parts.All(p => p is not null))
            {
                var inner = string.Concat(parts.Select(p => p!.Value));
                if (_depth >= MaxDynamicDepth)
                {
                    _warnings.Add($"Linha {LineOf(node)}: SQL dinâmico aninhado demais; o conteúdo não foi analisado.");
                    return;
                }
                var nested = SqlScriptAnalyzer.AnalyzeCore(inner, _depth + 1);
                foreach (var h in nested.Hits)
                {
                    Add(node, h.Danger.Kind, h.Danger.Target, "Dentro de EXEC: " + h.Danger.Description,
                        previewable: false, countQueries: h.Danger.CountQueries);
                }
                _warnings.AddRange(nested.Analysis.Warnings);
            }
            else
            {
                _warnings.Add($"Linha {LineOf(node)}: EXEC com SQL montado em tempo de execução; o conteúdo não pode ser analisado.");
            }
        }
        else if (entity is ExecutableProcedureReference { ProcedureReference.ProcedureReference.Name: { } name }
                 && string.Equals(name.BaseIdentifier?.Value, "sp_executesql", StringComparison.OrdinalIgnoreCase))
        {
            _warnings.Add($"Linha {LineOf(node)}: sp_executesql executa SQL dinâmico; o conteúdo não é analisado.");
        }
    }

    // ---------- utilitários ----------

    private void Add(TSqlStatement node, DangerKind kind, string? target, string description, bool previewable,
        IReadOnlyList<string>? countQueries = null)
    {
        var start = _batch.Start + node.StartOffset;
        var danger = new DangerousStatement(kind, start, node.FragmentLength, _map.LineOf(start), description, target,
            countQueries ?? [], previewable && _depth == 0);
        _hits.Add(new Hit(danger, _depth == 0 ? node : null, _batch.Index));
    }

    private int LineOf(TSqlFragment node) => _map.LineOf(_batch.Start + node.StartOffset);

    /// <summary>Nome do alvo de UPDATE/DELETE, resolvendo alias (<c>UPDATE c ... FROM dbo.Clientes c</c> vira dbo.Clientes).</summary>
    private static string ResolveTarget(TableReference? target, FromClause? from)
    {
        switch (target)
        {
            case NamedTableReference named:
                if (named.SchemaObject.Identifiers.Count == 1 && from is not null)
                {
                    var alias = named.SchemaObject.BaseIdentifier.Value;
                    var real = FindByAlias(from.TableReferences, alias);
                    if (real is not null) return Display(real.SchemaObject);
                }
                return Display(named.SchemaObject);
            case VariableTableReference v:
                return v.Variable.Name;
            default:
                return "(destino não identificado)";
        }
    }

    private static NamedTableReference? FindByAlias(IEnumerable<TableReference> refs, string alias)
    {
        foreach (var r in refs)
        {
            switch (r)
            {
                case NamedTableReference n when n.Alias is not null && string.Equals(n.Alias.Value, alias, StringComparison.OrdinalIgnoreCase):
                    return n;
                case QualifiedJoin q:
                    if (FindByAlias([q.FirstTableReference, q.SecondTableReference], alias) is { } fq) return fq;
                    break;
                case UnqualifiedJoin u:
                    if (FindByAlias([u.FirstTableReference, u.SecondTableReference], alias) is { } fu) return fu;
                    break;
                case JoinParenthesisTableReference p:
                    if (p.Join is not null && FindByAlias([p.Join], alias) is { } fp) return fp;
                    break;
            }
        }
        return null;
    }

    /// <summary>Nome como digitado, sem aspas/colchetes: <c>dbo.Clientes</c>.</summary>
    internal static string Display(SchemaObjectName name) => string.Join(".", name.Identifiers.Select(i => i.Value));

    /// <summary>Nome com colchetes para uso seguro em SQL gerado: <c>[dbo].[Clientes]</c>.</summary>
    internal static string Quoted(SchemaObjectName name) =>
        string.Join(".", name.Identifiers.Select(i => "[" + i.Value.Replace("]", "]]") + "]"));

    internal static string CountQuery(SchemaObjectName name) => $"SELECT COUNT_BIG(*) FROM {Quoted(name)}";
}
