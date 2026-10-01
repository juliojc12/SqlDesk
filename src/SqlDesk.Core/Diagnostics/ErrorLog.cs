using System.Text;

namespace SqlDesk.Core.Diagnostics;

/// <summary>
/// Registro de erros inesperados em arquivo (<c>%APPDATA%\SqlDesk\logs\error.log</c>), para diagnóstico. Grava só o tipo, a mensagem
/// e o stack da exceção, mais um contexto curto: nunca o texto das consultas, connection strings ou senhas. Gira o arquivo ao passar de 1 MB.
/// </summary>
public sealed class ErrorLog(string filePath, long maxBytes = 1_000_000)
{
    private readonly object _gate = new();

    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SqlDesk", "logs", "error.log");

    public string FilePath { get; } = filePath;

    /// <summary>Nunca lança: falhar ao registrar não pode derrubar quem já está tratando um erro.</summary>
    public void Write(string context, Exception ex)
    {
        try
        {
            lock (_gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                if (File.Exists(FilePath) && new FileInfo(FilePath).Length > maxBytes)
                    File.Move(FilePath, FilePath + ".1", overwrite: true);

                var sb = new StringBuilder();
                sb.Append('[').Append(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz")).Append("] ").AppendLine(context);
                sb.AppendLine(ex.ToString());
                sb.AppendLine();
                File.AppendAllText(FilePath, sb.ToString(), new UTF8Encoding(false));
            }
        }
        catch
        {
            /* sem disco ou sem permissão: segue sem registro */
        }
    }
}
