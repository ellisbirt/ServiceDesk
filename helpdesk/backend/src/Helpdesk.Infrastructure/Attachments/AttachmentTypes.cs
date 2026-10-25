using System.IO.Compression;
using System.Text;
using Helpdesk.Core.Interfaces;
using Helpdesk.Shared.Contracts;

namespace Helpdesk.Infrastructure.Attachments;

/// <summary>What a file really is, decided from its content and not from its name or the declared content type.</summary>
/// <param name="Kind">One of <see cref="AttachmentKinds"/>.</param>
/// <param name="ContentType">The canonical content type for the kind and extension.</param>
/// <param name="Extension">Lower-case extension with the dot.</param>
/// <param name="Text">For text files, the decoded text.</param>
public sealed record SniffedFile(string Kind, string ContentType, string Extension, string? Text);

/// <summary>
/// Accepts only the supported types: plain text (txt, log, csv, json, md), images (png, jpeg, gif, webp), PDF and the
/// modern Office formats (docx, xlsx, pptx). Video, audio, archives, executables, macro-enabled and legacy Office files
/// are refused. The content must match the extension.
/// </summary>
public static class AttachmentTypes
{
    private static readonly Dictionary<string, (string Kind, string ContentType)> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        [".txt"] = (AttachmentKinds.Text, "text/plain"),
        [".log"] = (AttachmentKinds.Text, "text/plain"),
        [".csv"] = (AttachmentKinds.Text, "text/csv"),
        [".json"] = (AttachmentKinds.Text, "application/json"),
        [".md"] = (AttachmentKinds.Text, "text/markdown"),
        [".png"] = (AttachmentKinds.Image, "image/png"),
        [".jpg"] = (AttachmentKinds.Image, "image/jpeg"),
        [".jpeg"] = (AttachmentKinds.Image, "image/jpeg"),
        [".gif"] = (AttachmentKinds.Image, "image/gif"),
        [".webp"] = (AttachmentKinds.Image, "image/webp"),
        [".pdf"] = (AttachmentKinds.Pdf, "application/pdf"),
        [".docx"] = (AttachmentKinds.Office, "application/vnd.openxmlformats-officedocument.wordprocessingml.document"),
        [".xlsx"] = (AttachmentKinds.Office, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"),
        [".pptx"] = (AttachmentKinds.Office, "application/vnd.openxmlformats-officedocument.presentationml.presentation"),
    };

    /// <summary>Every extension the service can handle, lower case with the dot.</summary>
    public static IReadOnlyList<string> SupportedExtensions { get; } = [.. Known.Keys.Select(k => k.ToLowerInvariant()).Order()];

    public static bool IsSupported(string extension) => Known.ContainsKey(extension);

    public static string KindOf(string extension) => Known[extension].Kind;

    public static string ContentTypeOf(string extension) => Known[extension].ContentType;

    /// <summary>The main part each Office extension must contain.</summary>
    private static readonly Dictionary<string, string> OfficeMainPart = new(StringComparer.OrdinalIgnoreCase)
    {
        [".docx"] = "word/document.xml",
        [".xlsx"] = "xl/workbook.xml",
        [".pptx"] = "ppt/presentation.xml",
    };

    /// <summary>
    /// Checks <paramref name="content"/> against its file name. Throws <see cref="AttachmentException"/> with code
    /// <c>unsupported_type</c> (415) or <c>unreadable_file</c> (422) when it must be refused.
    /// </summary>
    public static SniffedFile Sniff(string fileName, byte[] content, IReadOnlyCollection<string> allowedExtensions, AttachmentOptions limits)
    {
        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        if (!Known.ContainsKey(extension) || !allowedExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
        {
            throw Unsupported($"Files of type '{(extension.Length == 0 ? "(none)" : extension)}' are not accepted.");
        }

        var (kind, contentType) = Known[extension];
        switch (kind)
        {
            case AttachmentKinds.Image:
                if (content.Length > limits.MaxImageBytes)
                    throw new AttachmentException("file_too_large", $"Images can be at most {limits.MaxImageBytes} bytes.", 413);
                return new SniffedFile(kind, ImageType(content, extension), extension, null);

            case AttachmentKinds.Pdf:
                if (!StartsWith(content, "%PDF-", within: 1024)) throw Mismatch(extension);
                return new SniffedFile(kind, contentType, extension, null);

            case AttachmentKinds.Office:
                CheckOffice(content, extension, limits);
                return new SniffedFile(kind, contentType, extension, null);

            default:
                return new SniffedFile(kind, contentType, extension, DecodeText(content));
        }
    }

    private static string ImageType(byte[] c, string extension)
    {
        if (c.Length >= 8 && c[0] == 0x89 && c[1] == 0x50 && c[2] == 0x4E && c[3] == 0x47) return Match(extension, "image/png", ".png");
        if (c.Length >= 3 && c[0] == 0xFF && c[1] == 0xD8 && c[2] == 0xFF) return Match(extension, "image/jpeg", ".jpg", ".jpeg");
        if (c.Length >= 6 && c[0] == 'G' && c[1] == 'I' && c[2] == 'F' && c[3] == '8') return Match(extension, "image/gif", ".gif");
        if (c.Length >= 12 && StartsWith(c, "RIFF") && c[8] == 'W' && c[9] == 'E' && c[10] == 'B' && c[11] == 'P')
            return Match(extension, "image/webp", ".webp");
        throw Mismatch(extension);

        static string Match(string ext, string type, params string[] extensions) =>
            extensions.Contains(ext) ? type : throw Mismatch(ext);
    }

    private static void CheckOffice(byte[] content, string extension, AttachmentOptions limits)
    {
        // The old binary Office format, and password-protected OOXML, are both OLE compound files.
        if (content.Length >= 4 && content[0] == 0xD0 && content[1] == 0xCF && content[2] == 0x11 && content[3] == 0xE0)
            throw new AttachmentException("unreadable_file", "Password-protected and legacy Office files are not accepted. Use docx, xlsx or pptx without a password.", 422);
        if (!(content.Length >= 4 && content[0] == 'P' && content[1] == 'K')) throw Mismatch(extension);

        try
        {
            using var zip = new ZipArchive(new MemoryStream(content, writable: false), ZipArchiveMode.Read);
            if (zip.Entries.Count > limits.MaxOfficeEntries)
                throw new AttachmentException("unreadable_file", "The document has too many parts.", 422);
            long total = 0;
            foreach (var entry in zip.Entries)
            {
                total += entry.Length;
                if (total > limits.MaxOfficeUncompressedBytes)
                    throw new AttachmentException("unreadable_file", "The document is too large when unpacked.", 422);
                var name = entry.FullName;
                if (name.Contains("vbaProject", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".bin", StringComparison.OrdinalIgnoreCase)
                    && name.Contains("vba", StringComparison.OrdinalIgnoreCase))
                    throw Unsupported("Macro-enabled documents are not accepted.");
            }
            if (zip.GetEntry("[Content_Types].xml") is null || zip.GetEntry(OfficeMainPart[extension]) is null) throw Mismatch(extension);
            using var types = new StreamReader(zip.GetEntry("[Content_Types].xml")!.Open());
            if (types.ReadToEnd().Contains("macroEnabled", StringComparison.OrdinalIgnoreCase))
                throw Unsupported("Macro-enabled documents are not accepted.");
        }
        catch (InvalidDataException)
        {
            throw Mismatch(extension);
        }
    }

    /// <summary>Decodes a text file, refusing binary content and content that is really another file type.</summary>
    internal static string DecodeText(byte[] content)
    {
        if (content.Length == 0) throw new AttachmentException("unreadable_file", "The file is empty.", 422);
        if (LooksLikeAnotherType(content)) throw Mismatch(".txt");

        string text;
        if (content.Length >= 3 && content[0] == 0xEF && content[1] == 0xBB && content[2] == 0xBF)
        {
            text = Encoding.UTF8.GetString(content, 3, content.Length - 3);
        }
        else if (content.Length >= 2 && content[0] == 0xFF && content[1] == 0xFE)
        {
            text = Encoding.Unicode.GetString(content, 2, content.Length - 2);
        }
        else if (content.Length >= 2 && content[0] == 0xFE && content[1] == 0xFF)
        {
            text = Encoding.BigEndianUnicode.GetString(content, 2, content.Length - 2);
        }
        else
        {
            if (Array.IndexOf(content, (byte)0) >= 0) throw Mismatch(".txt");
            try
            {
                text = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(content);
            }
            catch (DecoderFallbackException)
            {
                // Windows tools often write in the local code page.
                text = Encoding.Latin1.GetString(content);
            }
        }

        var control = 0;
        foreach (var ch in text)
        {
            if (ch < ' ' && ch is not ('\t' or '\n' or '\r' or '\f' or '\u001b')) control++;
        }
        if (control > Math.Max(8, text.Length / 100)) throw Mismatch(".txt");
        return text;
    }

    private static bool LooksLikeAnotherType(byte[] c) =>
        StartsWith(c, "MZ") || StartsWith(c, "\u007fELF") || StartsWith(c, "PK") || StartsWith(c, "%PDF-") || StartsWith(c, "\u0089PNG")
        || StartsWith(c, "GIF8") || StartsWith(c, "Rar!") || (c.Length >= 3 && c[0] == 0xFF && c[1] == 0xD8 && c[2] == 0xFF)
        || (c.Length >= 4 && c[0] == 0xD0 && c[1] == 0xCF && c[2] == 0x11 && c[3] == 0xE0);

    private static bool StartsWith(byte[] content, string prefix, int within = 0)
    {
        var ascii = Encoding.Latin1.GetBytes(prefix);
        var limit = Math.Max(within, ascii.Length);
        for (var offset = 0; offset <= Math.Min(content.Length, limit) - ascii.Length; offset++)
        {
            if (content.AsSpan(offset, ascii.Length).SequenceEqual(ascii)) return true;
            if (within == 0) break;
        }
        return false;
    }

    private static AttachmentException Unsupported(string message) => new("unsupported_type", message, 415);

    private static AttachmentException Mismatch(string extension) => new(
        "unsupported_type", $"The file's content does not match its '{extension}' type, so it was not accepted.", 415);
}
