using SqlDesk.Core.Execution;

namespace SqlDesk.Core.Export;

public enum ExportFormat
{
    Csv,
    Xlsx,
}

public sealed record ExportResult(string Path, long Rows, long ElapsedMs);

/// <summary>O XLSX comporta no máximo 1.048.576 linhas por planilha (uma é o cabeçalho).</summary>
public sealed class ExportLimitException(string message) : Exception(message);

/// <summary>Escreve as linhas de um resultado em streaming; os valores chegam com o tipo do .NET (ou já convertidos de volta).</summary>
public interface IRowWriter : IDisposable
{
    long Rows { get; }

    void Begin(IReadOnlyList<ColumnInfo> columns);

    void Write(object?[] row, CancellationToken ct);

    /// <summary>Fecha o arquivo (espera o fim da escrita). Sem chamar isto, o arquivo fica incompleto.</summary>
    void Complete();
}
