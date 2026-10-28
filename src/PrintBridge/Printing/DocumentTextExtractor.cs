using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace PrintBridge.Printing;

public static class DocumentTextExtractor
{
    public static bool IsOfficeExtension(string extension) =>
        extension is ".docx" or ".xlsx" or ".pptx" or ".odt" or ".ods" or ".odp";

    public static bool IsTextFormat(string extension, string mimeType)
    {
        if (mimeType.StartsWith("text/", StringComparison.OrdinalIgnoreCase))
            return true;

        if (mimeType.Equals("application/json", StringComparison.OrdinalIgnoreCase)
            || mimeType.Contains("xml", StringComparison.OrdinalIgnoreCase)
            || mimeType.Contains("rtf", StringComparison.OrdinalIgnoreCase))
            return true;

        return extension is ".txt" or ".log" or ".csv" or ".tsv" or ".md" or ".markdown"
            or ".json" or ".xml" or ".html" or ".htm" or ".rtf" or ".ini" or ".config";
    }

    public static string DecodeText(byte[] data)
    {
        if (data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF)
            return Encoding.UTF8.GetString(data, 3, data.Length - 3);

        if (data.Length >= 2 && data[0] == 0xFF && data[1] == 0xFE)
            return Encoding.Unicode.GetString(data, 2, data.Length - 2);

        if (data.Length >= 2 && data[0] == 0xFE && data[1] == 0xFF)
            return Encoding.BigEndianUnicode.GetString(data, 2, data.Length - 2);

        try
        {
            return new UTF8Encoding(false, true).GetString(data);
        }
        catch
        {
            return Encoding.Latin1.GetString(data);
        }
    }

    public static string TextFromHtml(string html)
    {
        html = Regex.Replace(html, @"(?is)<(script|style)[^>]*>.*?</\1>", " ");
        html = Regex.Replace(html, @"(?i)<br\s*/?>", "\n");
        html = Regex.Replace(html, @"(?i)</t[dh]>", "\t");
        html = Regex.Replace(html, @"(?i)</(p|div|h[1-6]|tr|li|table|section|article|header|footer)>", "\n");
        html = Regex.Replace(html, @"(?s)<[^>]+>", " ");
        html = WebUtility.HtmlDecode(html);
        html = Regex.Replace(html, @"[ \t\f\v]+", " ");
        html = Regex.Replace(html, @"\n[ \t]+", "\n");
        html = Regex.Replace(html, @"\n{3,}", "\n\n");
        return html.Trim();
    }

    public static string TextFromRtf(string rtf)
    {
        rtf = Regex.Replace(rtf, @"\{\\\*[^{}]*\}", "");
        rtf = Regex.Replace(rtf, @"\\'([0-9a-fA-F]{2})",
            m => ((char)Convert.ToInt32(m.Groups[1].Value, 16)).ToString());
        rtf = rtf.Replace("\\par", "\n").Replace("\\line", "\n").Replace("\\tab", "\t");
        rtf = Regex.Replace(rtf, @"\\[a-zA-Z]+-?\d* ?", "");
        rtf = rtf.Replace("\\{", "{").Replace("\\}", "}").Replace("\\\\", "\\");
        rtf = rtf.Replace("{", "").Replace("}", "");
        return rtf;
    }

    public static bool LooksLikeText(byte[] data)
    {
        if (data.Length == 0) return false;

        int sample = Math.Min(data.Length, 4096);
        int printable = 0;
        for (int i = 0; i < sample; i++)
        {
            byte b = data[i];
            if (b == 9 || b == 10 || b == 13 || (b >= 32 && b < 127) || b >= 0xC2)
                printable++;
        }

        return printable >= sample * 0.9;
    }

    public static string? ExtractOfficeText(byte[] data, string extension)
    {
        try
        {
            using var zip = new ZipArchive(new MemoryStream(data), ZipArchiveMode.Read);
            return extension switch
            {
                ".docx" => ExtractDocx(zip),
                ".xlsx" => ExtractXlsx(zip),
                ".pptx" => ExtractPptx(zip),
                ".odt" or ".ods" or ".odp" => ExtractOpenDocument(zip),
                _ => null
            };
        }
        catch
        {
            return null;
        }
    }

    private static string? ExtractDocx(ZipArchive zip)
    {
        var entry = FindEntry(zip, "word/document.xml");
        if (entry == null) return null;

        using var stream = entry.Open();
        var doc = XDocument.Load(stream);
        var lines = new List<string>();

        foreach (var paragraph in doc.Descendants().Where(e => e.Name.LocalName == "p"))
        {
            var builder = new StringBuilder();
            foreach (var node in paragraph.Descendants())
            {
                switch (node.Name.LocalName)
                {
                    case "t":
                        builder.Append(node.Value);
                        break;
                    case "tab":
                        builder.Append('\t');
                        break;
                    case "br":
                    case "cr":
                        builder.Append('\n');
                        break;
                }
            }

            if (builder.Length > 0)
                lines.Add(builder.ToString());
        }

        return lines.Count > 0 ? string.Join("\n", lines) : null;
    }

    private static string? ExtractPptx(ZipArchive zip)
    {
        var slides = zip.Entries
            .Where(e => e.FullName.StartsWith("ppt/slides/slide", StringComparison.OrdinalIgnoreCase)
                        && e.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
            .OrderBy(e => NumberSuffix(e.FullName))
            .ToList();

        if (slides.Count == 0) return null;

        var output = new List<string>();
        foreach (var slide in slides)
        {
            using var stream = slide.Open();
            var doc = XDocument.Load(stream);

            foreach (var paragraph in doc.Descendants().Where(e => e.Name.LocalName == "p"))
            {
                var text = string.Concat(paragraph.Descendants()
                    .Where(e => e.Name.LocalName == "t")
                    .Select(e => e.Value));

                if (!string.IsNullOrWhiteSpace(text))
                    output.Add(text);
            }

            output.Add(string.Empty);
        }

        return output.Count > 0 ? string.Join("\n", output).Trim() : null;
    }

    private static string? ExtractXlsx(ZipArchive zip)
    {
        var shared = new List<string>();
        var sharedEntry = FindEntry(zip, "xl/sharedStrings.xml");
        if (sharedEntry != null)
        {
            using var stream = sharedEntry.Open();
            var doc = XDocument.Load(stream);
            foreach (var item in doc.Descendants().Where(e => e.Name.LocalName == "si"))
                shared.Add(string.Concat(item.Descendants()
                    .Where(e => e.Name.LocalName == "t")
                    .Select(e => e.Value)));
        }

        var sheets = zip.Entries
            .Where(e => e.FullName.StartsWith("xl/worksheets/sheet", StringComparison.OrdinalIgnoreCase)
                        && e.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
            .OrderBy(e => NumberSuffix(e.FullName))
            .ToList();

        if (sheets.Count == 0) return null;

        var output = new List<string>();
        foreach (var sheet in sheets)
        {
            using var stream = sheet.Open();
            var doc = XDocument.Load(stream);

            foreach (var row in doc.Descendants().Where(e => e.Name.LocalName == "row"))
            {
                var cells = new List<(int Column, string Value)>();
                foreach (var cell in row.Elements().Where(e => e.Name.LocalName == "c"))
                {
                    var reference = cell.Attribute("r")?.Value ?? string.Empty;
                    var type = cell.Attribute("t")?.Value ?? string.Empty;

                    string value;
                    if (type == "inlineStr")
                    {
                        value = string.Concat(cell.Descendants()
                            .Where(e => e.Name.LocalName == "t")
                            .Select(e => e.Value));
                    }
                    else
                    {
                        var raw = cell.Elements().FirstOrDefault(e => e.Name.LocalName == "v")?.Value ?? string.Empty;
                        if (type == "s" && int.TryParse(raw, out var index) && index >= 0 && index < shared.Count)
                            value = shared[index];
                        else
                            value = raw;
                    }

                    if (!string.IsNullOrEmpty(value))
                        cells.Add((ColumnIndex(reference), value));
                }

                if (cells.Count == 0) continue;

                int maxColumn = cells.Max(c => c.Column);
                var cellsByColumn = new string[maxColumn + 1];
                foreach (var (column, value) in cells)
                    cellsByColumn[column] = value;

                output.Add(string.Join("\t", cellsByColumn));
            }

            output.Add(string.Empty);
        }

        return output.Count > 0 ? string.Join("\n", output).Trim() : null;
    }

    private static string? ExtractOpenDocument(ZipArchive zip)
    {
        var entry = FindEntry(zip, "content.xml");
        if (entry == null) return null;

        using var stream = entry.Open();
        var doc = XDocument.Load(stream);
        var lines = doc.Descendants()
            .Where(e => e.Name.LocalName is "p" or "h")
            .Select(e => e.Value)
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .ToList();

        return lines.Count > 0 ? string.Join("\n", lines) : null;
    }

    private static ZipArchiveEntry? FindEntry(ZipArchive zip, string suffix) =>
        zip.Entries.FirstOrDefault(e => e.FullName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));

    private static int NumberSuffix(string name)
    {
        var file = Path.GetFileNameWithoutExtension(name);
        var digits = new string(file.Where(char.IsDigit).ToArray());
        return int.TryParse(digits, out var number) ? number : int.MaxValue;
    }

    private static int ColumnIndex(string reference)
    {
        int column = 0;
        foreach (var c in reference)
        {
            if (c >= 'A' && c <= 'Z')
                column = column * 26 + (c - 'A' + 1);
            else if (c >= 'a' && c <= 'z')
                column = column * 26 + (c - 'a' + 1);
            else
                break;
        }

        return Math.Max(0, column - 1);
    }
}
