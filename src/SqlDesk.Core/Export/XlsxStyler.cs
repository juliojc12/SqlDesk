using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace SqlDesk.Core.Export;

/// <summary>
/// O MiniExcel não tem opção de negrito. Depois que ele termina, ajusta só o <c>styles.xml</c> (pequeno): cria uma fonte em negrito
/// e a aplica ao estilo das células do cabeçalho. As demais entradas do zip (a planilha, que pode ter centenas de MB) não são tocadas.
/// </summary>
public static class XlsxStyler
{
    private static readonly XNamespace Main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

    /// <param name="xlsx">O arquivo já escrito; precisa permitir leitura, escrita e posicionamento.</param>
    public static void BoldHeader(Stream xlsx)
    {
        using var zip = new ZipArchive(xlsx, ZipArchiveMode.Update, leaveOpen: true);

        var headerStyle = HeaderStyleIndex(zip);
        var entry = zip.GetEntry("xl/styles.xml");
        if (headerStyle is null || entry is null) return; // arquivo inesperado: melhor sem negrito do que corrompido

        XDocument doc;
        using (var s = entry.Open()) doc = XDocument.Load(s);

        var fonts = doc.Root?.Element(Main + "fonts");
        var xfs = doc.Root?.Element(Main + "cellXfs")?.Elements(Main + "xf").ToList();
        if (fonts is null || xfs is null || headerStyle >= xfs.Count) return;

        var fontIndex = fonts.Elements(Main + "font").Count();
        fonts.Add(new XElement(Main + "font", new XElement(Main + "b"), new XElement(Main + "sz", new XAttribute("val", "11")), new XElement(Main + "name", new XAttribute("val", "Calibri"))));
        fonts.SetAttributeValue("count", fontIndex + 1);

        var xf = xfs[headerStyle.Value];
        xf.SetAttributeValue("fontId", fontIndex);
        xf.SetAttributeValue("applyFont", "1");

        entry.Delete();
        var replaced = zip.CreateEntry("xl/styles.xml", CompressionLevel.Fastest);
        using var w = replaced.Open();
        doc.Save(w);
    }

    /// <summary>Índice do estilo da célula A1 (o cabeçalho), lendo só o começo da planilha.</summary>
    private static int? HeaderStyleIndex(ZipArchive zip)
    {
        var sheet = zip.GetEntry("xl/worksheets/sheet1.xml");
        if (sheet is null) return null;
        using var s = sheet.Open();
        var buffer = new byte[16 * 1024];
        var read = 0;
        while (read < buffer.Length)
        {
            var n = s.Read(buffer, read, buffer.Length - read);
            if (n == 0) break;
            read += n;
        }
        var head = Encoding.UTF8.GetString(buffer, 0, read);
        var m = Regex.Match(head, "<(?:\\w+:)?c [^>]*r=\"A1\"[^>]*?\\bs=\"(\\d+)\"");
        return m.Success ? int.Parse(m.Groups[1].Value) : null;
    }
}
