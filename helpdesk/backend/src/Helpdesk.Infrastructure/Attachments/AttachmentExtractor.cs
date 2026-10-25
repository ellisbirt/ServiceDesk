using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using Helpdesk.Core.Interfaces;
using Helpdesk.Shared.Contracts;
using UglyToad.PdfPig;

namespace Helpdesk.Infrastructure.Attachments;

/// <param name="Text">The file's text, capped at <see cref="AttachmentOptions.MaxExtractedChars"/>.</param>
/// <param name="Truncated">The cap was reached and the end was dropped.</param>
/// <param name="NoText">A PDF with (almost) no text layer, such as a scan: the model needs the PDF itself to read it.</param>
public sealed record ExtractedText(string Text, bool Truncated, bool NoText = false);

/// <summary>
/// Reads the text out of Office documents and PDFs. Office files are read as XML inside the zip, never opened by an Office
/// program, so macros and embedded objects are never run. Every read is bounded by the limits in <see cref="AttachmentOptions"/>.
/// </summary>
public static partial class AttachmentExtractor
{
    public static ExtractedText Extract(SniffedFile file, byte[] content, AttachmentOptions limits)
    {
        try
        {
            return file.Kind switch
            {
                AttachmentKinds.Text => Cap(file.Text ?? "", limits),
                AttachmentKinds.Pdf => Pdf(content, limits),
                AttachmentKinds.Office => Office(file.Extension, content, limits),
                _ => new ExtractedText("", false)
            };
        }
        catch (Exception ex) when (ex is XmlException or InvalidDataException or IOException or InvalidOperationException
            or ArgumentException or NotSupportedException or UglyToad.PdfPig.Core.PdfDocumentFormatException)
        {
            throw new AttachmentException("unreadable_file", "The file could not be read. It may be damaged or password-protected.", 422);
        }
    }

    private static ExtractedText Cap(string text, AttachmentOptions limits) =>
        text.Length <= limits.MaxExtractedChars ? new ExtractedText(text, false) : new ExtractedText(text[..limits.MaxExtractedChars], true);

    // ----- PDF -----

    private static ExtractedText Pdf(byte[] content, AttachmentOptions limits)
    {
        using var document = PdfDocument.Open(content);
        var text = new StringBuilder();
        var pages = 0;
        var truncated = false;
        foreach (var page in document.GetPages())
        {
            if (pages >= limits.MaxPdfPages || text.Length >= limits.MaxExtractedChars)
            {
                truncated = true;
                break;
            }
            pages++;
            var pageText = page.Text.Trim();
            if (text.Length > 0) text.Append("\n\n");
            text.Append($"--- Page {page.Number} ---\n").Append(pageText);
        }

        var body = text.ToString();
        // A scan has a page image and no text; "--- Page N ---" markers alone do not count as text.
        var letters = body.Count(char.IsLetterOrDigit) - pages * 5;
        var noText = pages > 0 && letters / pages < 25;
        var capped = Cap(body, limits);
        return capped with { Truncated = capped.Truncated || truncated, NoText = noText };
    }

    // ----- Office -----

    private static ExtractedText Office(string extension, byte[] content, AttachmentOptions limits)
    {
        using var zip = new ZipArchive(new MemoryStream(content, writable: false), ZipArchiveMode.Read);
        var text = new StringBuilder();
        switch (extension)
        {
            case ".docx": Docx(zip, text, limits); break;
            case ".pptx": Pptx(zip, text, limits); break;
            case ".xlsx": Xlsx(zip, text, limits); break;
        }
        return Cap(text.ToString(), limits);
    }

    /// <summary>
    /// The text inside the current element, leaving the reader on its end tag. (ReadElementContentAsString moves past the end
    /// tag, so a loop that then calls Read() would skip the node after it.)
    /// </summary>
    private static string ReadText(XmlReader reader)
    {
        if (reader.IsEmptyElement) return "";
        var depth = reader.Depth;
        var sb = new StringBuilder();
        while (reader.Read() && reader.Depth > depth)
        {
            if (reader.NodeType is XmlNodeType.Text or XmlNodeType.CDATA or XmlNodeType.SignificantWhitespace or XmlNodeType.Whitespace)
                sb.Append(reader.Value);
        }
        return sb.ToString();
    }

    private static XmlReader Open(ZipArchive zip, string path, AttachmentOptions limits)
    {
        var entry = zip.GetEntry(path) ?? throw new InvalidDataException($"Missing part {path}.");
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = limits.MaxOfficeUncompressedBytes,
            IgnoreComments = true,
            IgnoreWhitespace = false
        };
        return XmlReader.Create(entry.Open(), settings);
    }

    private static void Docx(ZipArchive zip, StringBuilder text, AttachmentOptions limits)
    {
        using var reader = Open(zip, "word/document.xml", limits);
        while (reader.Read() && text.Length < limits.MaxExtractedChars)
        {
            switch (reader.NodeType)
            {
                case XmlNodeType.Element when reader.LocalName == "t":
                    text.Append(ReadText(reader));
                    break;
                case XmlNodeType.Element when reader.LocalName is "tab":
                    text.Append('\t');
                    break;
                case XmlNodeType.Element when reader.LocalName is "br" or "cr":
                    text.Append('\n');
                    break;
                case XmlNodeType.EndElement when reader.LocalName == "p":
                    text.Append('\n');
                    break;
                case XmlNodeType.EndElement when reader.LocalName == "tc":
                    text.Append('\t');
                    break;
            }
        }
    }

    private static void Pptx(ZipArchive zip, StringBuilder text, AttachmentOptions limits)
    {
        var slides = zip.Entries
            .Select(e => (Entry: e, Match: SlidePath().Match(e.FullName)))
            .Where(x => x.Match.Success)
            .OrderBy(x => int.Parse(x.Match.Groups[1].Value))
            .ToList();
        foreach (var (entry, match) in slides)
        {
            if (text.Length >= limits.MaxExtractedChars) return;
            text.Append($"--- Slide {match.Groups[1].Value} ---\n");
            AppendDrawingText(zip, entry.FullName, text, limits);
            var notes = $"ppt/notesSlides/notesSlide{match.Groups[1].Value}.xml";
            if (zip.GetEntry(notes) is not null)
            {
                var before = text.Length;
                text.Append("Notes:\n");
                AppendDrawingText(zip, notes, text, limits);
                if (text.Length - before <= "Notes:\n".Length + 1) text.Length = before;
            }
            text.Append('\n');
        }
    }

    private static void AppendDrawingText(ZipArchive zip, string path, StringBuilder text, AttachmentOptions limits)
    {
        using var reader = Open(zip, path, limits);
        while (reader.Read() && text.Length < limits.MaxExtractedChars)
        {
            if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "t") text.Append(ReadText(reader));
            else if (reader.NodeType == XmlNodeType.EndElement && reader.LocalName == "p") text.Append('\n');
        }
    }

    private static void Xlsx(ZipArchive zip, StringBuilder text, AttachmentOptions limits)
    {
        var shared = SharedStrings(zip, limits);
        var sheets = SheetNames(zip, limits);
        foreach (var (name, path) in sheets)
        {
            if (text.Length >= limits.MaxExtractedChars) return;
            if (zip.GetEntry(path) is null) continue;
            text.Append($"--- Sheet: {name} ---\n");
            using var reader = Open(zip, path, limits);
            var row = new List<string>();
            string? type = null;
            while (reader.Read() && text.Length < limits.MaxExtractedChars)
            {
                if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "c")
                {
                    type = reader.GetAttribute("t");
                }
                else if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "v")
                {
                    var value = ReadText(reader);
                    row.Add(type == "s" && int.TryParse(value, out var index) && index >= 0 && index < shared.Count ? shared[index] : value);
                }
                else if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "is")
                {
                    row.Add(ReadInlineString(reader));
                }
                else if (reader.NodeType == XmlNodeType.EndElement && reader.LocalName == "row")
                {
                    if (row.Count > 0) text.Append(string.Join('\t', row)).Append('\n');
                    row.Clear();
                }
            }
        }
    }

    private static string ReadInlineString(XmlReader reader)
    {
        var sb = new StringBuilder();
        var depth = reader.Depth;
        while (reader.Read() && reader.Depth > depth)
        {
            if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "t") sb.Append(ReadText(reader));
        }
        return sb.ToString();
    }

    private static List<string> SharedStrings(ZipArchive zip, AttachmentOptions limits)
    {
        var strings = new List<string>();
        if (zip.GetEntry("xl/sharedStrings.xml") is null) return strings;
        using var reader = Open(zip, "xl/sharedStrings.xml", limits);
        var current = new StringBuilder();
        while (reader.Read() && strings.Count < 1_000_000)
        {
            if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "t") current.Append(ReadText(reader));
            else if (reader.NodeType == XmlNodeType.EndElement && reader.LocalName == "si")
            {
                strings.Add(current.ToString());
                current.Clear();
            }
        }
        return strings;
    }

    private static List<(string Name, string Path)> SheetNames(ZipArchive zip, AttachmentOptions limits)
    {
        var targets = new Dictionary<string, string>(StringComparer.Ordinal);
        if (zip.GetEntry("xl/_rels/workbook.xml.rels") is not null)
        {
            using var rels = Open(zip, "xl/_rels/workbook.xml.rels", limits);
            while (rels.Read())
            {
                if (rels.NodeType == XmlNodeType.Element && rels.LocalName == "Relationship"
                    && rels.GetAttribute("Id") is { } id && rels.GetAttribute("Target") is { } target)
                {
                    targets[id] = target.StartsWith('/') ? target.TrimStart('/') : "xl/" + target;
                }
            }
        }

        var sheets = new List<(string, string)>();
        using var workbook = Open(zip, "xl/workbook.xml", limits);
        while (workbook.Read())
        {
            if (workbook.NodeType == XmlNodeType.Element && workbook.LocalName == "sheet")
            {
                var name = workbook.GetAttribute("name") ?? "Sheet";
                var id = workbook.GetAttribute("id", "http://schemas.openxmlformats.org/officeDocument/2006/relationships");
                if (id is not null && targets.TryGetValue(id, out var path)) sheets.Add((name, path));
            }
        }
        return sheets;
    }

    [GeneratedRegex(@"^ppt/slides/slide(\d+)\.xml$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex SlidePath();
}
