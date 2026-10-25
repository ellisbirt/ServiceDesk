using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Helpdesk.Core.Interfaces;
using Helpdesk.Dlp;
using Helpdesk.Infrastructure.Ai;
using Helpdesk.Infrastructure.Attachments;
using Helpdesk.Llm;
using Helpdesk.Llm.Anthropic;
using Helpdesk.Llm.OpenAi;
using Helpdesk.Shared.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Helpdesk.Api.Tests;

/// <summary>Builds small real files for the tests.</summary>
internal static class Files
{
    public static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0];
    public static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0, 0x10, (byte)'J', (byte)'F', (byte)'I', (byte)'F'];

    public static byte[] Text(string text) => Encoding.UTF8.GetBytes(text);

    public static byte[] Zip(params (string Name, string Content)[] entries)
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                using var writer = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false));
                writer.Write(content);
            }
        }
        return stream.ToArray();
    }

    private const string Types = "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"></Types>";

    public static byte[] Docx(string text) => Zip(
        ("[Content_Types].xml", Types),
        ("word/document.xml",
            $"<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body><w:p><w:r><w:t>{text}</w:t></w:r></w:p><w:p><w:r><w:t>Second paragraph</w:t></w:r></w:p></w:body></w:document>"));

    public static byte[] Pptx(string text) => Zip(
        ("[Content_Types].xml", Types),
        ("ppt/presentation.xml", "<p:presentation xmlns:p=\"http://schemas.openxmlformats.org/presentationml/2006/main\"/>"),
        ("ppt/slides/slide2.xml", "<p:sld xmlns:p=\"p\" xmlns:a=\"a\"><a:p><a:r><a:t>Slide two</a:t></a:r></a:p></p:sld>"),
        ("ppt/slides/slide1.xml", $"<p:sld xmlns:p=\"p\" xmlns:a=\"a\"><a:p><a:r><a:t>{text}</a:t></a:r></a:p></p:sld>"),
        ("ppt/notesSlides/notesSlide1.xml", "<p:notes xmlns:p=\"p\" xmlns:a=\"a\"><a:p><a:r><a:t>Speaker notes here</a:t></a:r></a:p></p:notes>"));

    public static byte[] Xlsx() => Zip(
        ("[Content_Types].xml", Types),
        ("xl/workbook.xml",
            "<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets><sheet name=\"Devices\" sheetId=\"1\" r:id=\"rId1\"/></sheets></workbook>"),
        ("xl/_rels/workbook.xml.rels",
            "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Target=\"worksheets/sheet1.xml\" Type=\"x\"/></Relationships>"),
        ("xl/sharedStrings.xml", "<sst xmlns=\"x\"><si><t>Host</t></si><si><t>laptop-17</t></si></sst>"),
        ("xl/worksheets/sheet1.xml",
            "<worksheet xmlns=\"x\"><sheetData><row r=\"1\"><c r=\"A1\" t=\"s\"><v>0</v></c><c r=\"B1\" t=\"inlineStr\"><is><t>Age</t></is></c></row><row r=\"2\"><c r=\"A2\" t=\"s\"><v>1</v></c><c r=\"B2\"><v>42</v></c></row></sheetData></worksheet>"));

    /// <summary>A one-page PDF. With no text it is what a scan looks like to a text extractor.</summary>
    public static byte[] Pdf(string? text)
    {
        var content = text is null ? "" : $"BT /F1 12 Tf 10 100 Td ({text}) Tj ET";
        var objects = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 600 200] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>",
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>"
        };
        var sb = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (var i = 0; i < objects.Length; i++)
        {
            offsets.Add(sb.Length);
            sb.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }
        var xref = sb.Length;
        sb.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets) sb.Append($"{offset:D10} 00000 n \n");
        sb.Append($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }
}

public class AttachmentTypeTests
{
    private static readonly AttachmentOptions Limits = new();
    private static IReadOnlyCollection<string> All => AttachmentTypes.SupportedExtensions;

    private static SniffedFile Sniff(string name, byte[] content) => AttachmentTypes.Sniff(name, content, All, Limits);

    [Theory]
    [InlineData("notes.txt")]
    [InlineData("app.log")]
    [InlineData("data.csv")]
    [InlineData("config.json")]
    [InlineData("readme.md")]
    public void TextFiles_AreAccepted_AndDecoded(string name)
    {
        var sniffed = Sniff(name, Files.Text("line one\nline two\n"));

        Assert.Equal(AttachmentKinds.Text, sniffed.Kind);
        Assert.Equal("line one\nline two\n", sniffed.Text);
    }

    [Fact]
    public void Text_InOtherEncodings_IsDecoded()
    {
        Assert.Equal("héllo", Sniff("a.txt", [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes("héllo")]).Text);
        Assert.Equal("héllo", Sniff("a.txt", [.. Encoding.Unicode.GetPreamble(), .. Encoding.Unicode.GetBytes("héllo")]).Text);
        Assert.Equal("café", Sniff("a.log", Encoding.Latin1.GetBytes("café")).Text); // not valid UTF-8: Windows code page
    }

    [Fact]
    public void Images_AreAcceptedByTheirContent_NotTheirName()
    {
        Assert.Equal("image/png", Sniff("shot.png", Files.Png).ContentType);
        Assert.Equal("image/jpeg", Sniff("photo.jpeg", Files.Jpeg).ContentType);
        Assert.Equal("image/webp", Sniff("p.webp", [.. "RIFF"u8, 0, 0, 0, 0, .. "WEBP"u8, 0]).ContentType);
        Assert.Equal("image/gif", Sniff("a.gif", [.. "GIF89a"u8, 0, 0]).ContentType);
        Assert.Equal("unsupported_type", Assert.Throws<AttachmentException>(() => Sniff("shot.jpg", Files.Png)).Code);
    }

    [Fact]
    public void ImagesOverTheirOwnLimit_AreRefused()
    {
        var big = new byte[(int)Limits.MaxImageBytes + 1];
        Files.Png.CopyTo(big, 0);

        Assert.Equal("file_too_large", Assert.Throws<AttachmentException>(() => Sniff("big.png", big)).Code);
    }

    [Fact]
    public void ModernOfficeAndPdf_AreAccepted()
    {
        Assert.Equal(AttachmentKinds.Office, Sniff("a.docx", Files.Docx("x")).Kind);
        Assert.Equal(AttachmentKinds.Office, Sniff("a.xlsx", Files.Xlsx()).Kind);
        Assert.Equal(AttachmentKinds.Office, Sniff("a.pptx", Files.Pptx("x")).Kind);
        Assert.Equal(AttachmentKinds.Pdf, Sniff("a.pdf", Files.Pdf("hello")).Kind);
    }

    [Theory]
    [InlineData("clip.mp4")]
    [InlineData("song.mp3")]
    [InlineData("bundle.zip")]
    [InlineData("tool.exe")]
    [InlineData("old.doc")]
    [InlineData("old.xls")]
    [InlineData("old.ppt")]
    [InlineData("macro.docm")]
    [InlineData("mail.eml")]
    [InlineData("noextension")]
    public void OtherTypes_AreRefused(string name)
    {
        var error = Assert.Throws<AttachmentException>(() => Sniff(name, Files.Text("anything")));

        Assert.Equal("unsupported_type", error.Code);
        Assert.Equal(415, error.Status);
    }

    [Fact]
    public void ARenamedProgramOrArchive_IsNotAcceptedAsText()
    {
        Assert.Throws<AttachmentException>(() => Sniff("notes.txt", [.. "MZ"u8, 0x90, 0, 3, 0, 0, 0]));
        Assert.Throws<AttachmentException>(() => Sniff("notes.log", Files.Zip(("a", "b"))));
        Assert.Throws<AttachmentException>(() => Sniff("notes.txt", Files.Pdf("hi")));
        Assert.Throws<AttachmentException>(() => Sniff("notes.csv", Files.Png));
        Assert.Throws<AttachmentException>(() => Sniff("notes.txt", [0, 1, 2, 3, 0, 5, 6, 0]));
    }

    [Fact]
    public void ContentThatDoesNotMatchTheExtension_IsRefused()
    {
        Assert.Throws<AttachmentException>(() => Sniff("a.pdf", Files.Text("not a pdf")));
        Assert.Throws<AttachmentException>(() => Sniff("a.docx", Files.Text("not a zip")));
        Assert.Throws<AttachmentException>(() => Sniff("a.docx", Files.Xlsx())); // a spreadsheet named like a document
        Assert.Throws<AttachmentException>(() => Sniff("a.xlsx", Files.Zip(("hello.txt", "x"))));
    }

    [Fact]
    public void MacroEnabledAndPasswordProtectedOffice_AreRefused()
    {
        var withMacros = Files.Zip(
            ("[Content_Types].xml", "<Types/>"), ("word/document.xml", "<w:document xmlns:w=\"w\"/>"), ("word/vbaProject.bin", "x"));
        var ole = new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 };

        Assert.Contains("Macro", Assert.Throws<AttachmentException>(() => Sniff("a.docx", withMacros)).Message);
        Assert.Equal("unreadable_file", Assert.Throws<AttachmentException>(() => Sniff("a.docx", ole)).Code);
    }

    [Fact]
    public void AnOfficeFileThatUnpacksTooLarge_IsRefused()
    {
        var bomb = Files.Zip(
            ("[Content_Types].xml", "<Types/>"), ("word/document.xml", new string('a', 4096)));
        var tight = new AttachmentOptions { MaxOfficeUncompressedBytes = 1024 };

        Assert.Equal("unreadable_file",
            Assert.Throws<AttachmentException>(() => AttachmentTypes.Sniff("a.docx", bomb, All, tight)).Code);
    }

    [Fact]
    public void TheAllowedList_CanBeNarrowed()
    {
        string[] onlyText = [".txt", ".log"];

        Assert.Equal(AttachmentKinds.Text, AttachmentTypes.Sniff("a.log", Files.Text("x"), onlyText, Limits).Kind);
        Assert.Throws<AttachmentException>(() => AttachmentTypes.Sniff("a.png", Files.Png, onlyText, Limits));
    }
}

public class AttachmentExtractionTests
{
    private static readonly AttachmentOptions Limits = new();

    private static ExtractedText Extract(string name, byte[] content)
    {
        var sniffed = AttachmentTypes.Sniff(name, content, AttachmentTypes.SupportedExtensions, Limits);
        return AttachmentExtractor.Extract(sniffed, content, Limits);
    }

    [Fact]
    public void Docx_YieldsItsParagraphs() =>
        Assert.Equal("Hello docx\nSecond paragraph\n", Extract("a.docx", Files.Docx("Hello docx")).Text);

    [Fact]
    public void Pptx_YieldsSlidesInOrder_WithNotes()
    {
        var text = Extract("a.pptx", Files.Pptx("Slide one")).Text;

        Assert.True(text.IndexOf("Slide one", StringComparison.Ordinal) < text.IndexOf("Slide two", StringComparison.Ordinal));
        Assert.Contains("--- Slide 1 ---", text);
        Assert.Contains("Speaker notes here", text);
    }

    [Fact]
    public void Xlsx_YieldsSheetsAsRows_ResolvingSharedAndInlineStrings()
    {
        var text = Extract("a.xlsx", Files.Xlsx()).Text;

        Assert.Contains("--- Sheet: Devices ---", text);
        Assert.Contains("Host\tAge", text);
        Assert.Contains("laptop-17\t42", text);
    }

    [Fact]
    public void Pdf_YieldsItsText()
    {
        var result = Extract("a.pdf", Files.Pdf("The printer on the third floor reports a paper jam in tray two."));

        Assert.Contains("paper jam in tray two", result.Text);
        Assert.False(result.NoText);
    }

    [Fact]
    public void APdfWithoutText_IsFlaggedAsAScan() => Assert.True(Extract("scan.pdf", Files.Pdf(null)).NoText);

    [Fact]
    public void Text_IsCappedAtTheLimit()
    {
        var small = new AttachmentOptions { MaxExtractedChars = 100 };
        var sniffed = AttachmentTypes.Sniff("a.log", Files.Text(new string('x', 500)), AttachmentTypes.SupportedExtensions, small);

        var result = AttachmentExtractor.Extract(sniffed, Files.Text(new string('x', 500)), small);

        Assert.Equal(100, result.Text.Length);
        Assert.True(result.Truncated);
    }

    [Fact]
    public void ADamagedOfficeFile_IsReportedAsUnreadable_NotAsACrash()
    {
        var broken = Files.Zip(("[Content_Types].xml", "<Types/>"), ("word/document.xml", "<w:document><unclosed>"));

        Assert.Equal("unreadable_file", Assert.Throws<AttachmentException>(() => Extract("a.docx", broken)).Code);
    }

    [Fact]
    public void AnXmlBomb_IsNotExpanded()
    {
        var bomb = Files.Zip(
            ("[Content_Types].xml", "<Types/>"),
            ("word/document.xml",
                "<!DOCTYPE lol [<!ENTITY a \"aaaaaaaaaa\"><!ENTITY b \"&a;&a;&a;&a;&a;&a;&a;&a;\">]><w:document xmlns:w=\"w\"><w:t>&b;</w:t></w:document>"));

        Assert.Equal("unreadable_file", Assert.Throws<AttachmentException>(() => Extract("a.docx", bomb)).Code);
    }

    [Fact]
    public void ACorruptPdf_IsReportedAsUnreadable()
    {
        var corrupt = Encoding.Latin1.GetBytes("%PDF-1.4\n1 0 obj\n<< garbage");

        Assert.Equal("unreadable_file", Assert.Throws<AttachmentException>(() => Extract("a.pdf", corrupt)).Code);
    }
}

public class ContentReducerTests
{
    private static string Log(int lines) =>
        string.Join('\n', Enumerable.Range(1, lines).Select(i => $"2026-10-24 10:00:{i % 60:D2} INFO heartbeat ok seq={i}"));

    [Fact]
    public void ShortText_IsLeftAlone()
    {
        var (text, reduced) = ContentReducer.Reduce("one\ntwo", 1000);

        Assert.Equal("one\ntwo", text);
        Assert.False(reduced);
    }

    [Fact]
    public void ALongLog_KeepsTheStart_TheEnd_AndTheFailuresInTheMiddle()
    {
        var lines = Log(5000).Split('\n').ToList();
        lines[2500] = "2026-10-24 10:41:00 ERROR NullReferenceException in Orders.Save";
        lines[2501] = "   at Orders.Save() in Orders.cs:line 42";

        var (text, reduced) = ContentReducer.Reduce(string.Join('\n', lines), 2000);

        Assert.True(reduced);
        Assert.Contains("seq=1\n", text + "\n");
        Assert.Contains("seq=5000", text);
        Assert.Contains("NullReferenceException in Orders.Save", text);
        Assert.Contains("Orders.cs:line 42", text);
        Assert.Contains("lines omitted", text);
        Assert.True(ContentReducer.Tokens(text) <= 2200, $"{ContentReducer.Tokens(text)} tokens");
    }

    [Fact]
    public void ASingleHugeLine_IsCutFromTheMiddle()
    {
        var (text, reduced) = ContentReducer.Reduce("start" + new string('x', 100_000) + "end", 500);

        Assert.True(reduced);
        Assert.StartsWith("start", text);
        Assert.EndsWith("end", text);
        Assert.Contains("characters omitted", text);
    }

    [Fact]
    public void VeryLongLines_AreClipped()
    {
        var (text, _) = ContentReducer.Reduce(string.Join('\n', Enumerable.Repeat(new string('y', 5000), 50)), 800);

        Assert.DoesNotContain(new string('y', 1000), text);
    }
}
