using System.Globalization;
using System.Text;
using System.IO.Compression;
using InactivePDF.Domain.Models;
using InactivePDF.Infrastructure.IO;

namespace InactivePDF.Infrastructure.Rendering;

internal sealed class StreamingPdfDocument : IDisposable
{
    private readonly FileStream _stream;
    private readonly List<long> _offsets = [0];
    private readonly List<int> _pageReferences = [];
    private bool _completed;

    public StreamingPdfDocument(string outputPath, PdfOutputProfile profile)
    {
        _stream = new FileStream(
            outputPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.Read,
            64 * 1024,
            FileOptions.SequentialScan);

        WriteAscii($"%PDF-{profile.PdfVersion / 10}.{profile.PdfVersion % 10}\n%\xE2\xE3\xCF\xD3\n");
        WriteObject(1, "<< /Type /Catalog /Pages 2 0 R >>");
        WriteObject(3, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>");
        WriteObject(4, $"<< /Creator (InactivePDF) /Subject (InactivePDF {profile.Name} profile) >>");
    }

    public void AddTextPage(IReadOnlyList<string> lines, double pageWidth, double pageHeight, double margin, double fontSize, double lineHeight)
    {
        ObjectNumber contentObject = AllocateObject();
        ObjectNumber pageObject = AllocateObject();
        var content = BuildPageContent(lines, pageWidth, pageHeight, margin, fontSize, lineHeight);
        WriteCompressedStream(contentObject.Value, content);
        WriteObject(
            pageObject.Value,
            $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {PdfNumber(pageWidth)} {PdfNumber(pageHeight)}] /Resources << /Font << /F1 3 0 R >> >> /Contents {contentObject.Value} 0 R >>");
        _pageReferences.Add(pageObject.Value);
    }

    public void Complete()
    {
        if (_completed) return;
        _completed = true;
        var kids = string.Join(' ', _pageReferences.Select(reference => $"{reference} 0 R"));
        WriteObject(2, $"<< /Type /Pages /Kids [{kids}] /Count {_pageReferences.Count} >>");

        var xrefOffset = _stream.Position;
        WriteAscii($"xref\n0 {_offsets.Count}\n0000000000 65535 f \n");
        for (var index = 1; index < _offsets.Count; index++)
        {
            WriteAscii($"{_offsets[index]:D10} 00000 n \n");
        }

        WriteAscii($"trailer\n<< /Size {_offsets.Count} /Root 1 0 R /Info 4 0 R >>\nstartxref\n{xrefOffset}\n%%EOF\n");
        _stream.Flush(flushToDisk: true);
    }

    public void Dispose()
    {
        if (!_completed) Complete();
        _stream.Dispose();
    }

    private void WriteCompressedStream(int objectNumber, byte[] content)
    {
        using var compressed = new MemoryStream(content.Length);
        using (var deflater = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
        {
            deflater.Write(content);
        }

        WriteObjectHeader(objectNumber);
        WriteAscii($"<< /Length {compressed.Length} /Filter /FlateDecode >>\nstream\n");
        _stream.Write(compressed.GetBuffer(), 0, checked((int)compressed.Length));
        WriteAscii("\nendstream\nendobj\n");
    }

    private static byte[] BuildPageContent(IReadOnlyList<string> lines, double pageWidth, double pageHeight, double margin, double fontSize, double lineHeight)
    {
        using var content = new MemoryStream(capacity: Math.Max(512, lines.Count * 96));
        WriteAscii(content, "BT\n");
        WriteAscii(content, $"/F1 {PdfNumber(fontSize)} Tf\n");
        WriteAscii(content, "0 g\n");
        for (var index = 0; index < lines.Count; index++)
        {
            var y = pageHeight - margin - ((index + 1) * lineHeight);
            WriteAscii(content, $"1 0 0 1 {PdfNumber(margin)} {PdfNumber(y)} Tm\n");
            WritePdfString(content, lines[index]);
            WriteAscii(content, " Tj\n");
        }

        WriteAscii(content, "ET\n");
        return content.ToArray();
    }

    private ObjectNumber AllocateObject()
    {
        _offsets.Add(0);
        return new ObjectNumber(_offsets.Count - 1);
    }

    private void WriteObject(int number, string body)
    {
        EnsureOffsetSlot(number);
        WriteObjectHeader(number);
        WriteAscii(body);
        WriteAscii("\nendobj\n");
    }

    private void WriteObjectHeader(int number)
    {
        EnsureOffsetSlot(number);
        _offsets[number] = _stream.Position;
        WriteAscii($"{number} 0 obj\n");
    }

    private void EnsureOffsetSlot(int number)
    {
        while (_offsets.Count <= number) _offsets.Add(0);
        if (_offsets[number] != 0) throw new InvalidOperationException($"PDF object {number} was already written.");
    }

    private void WriteAscii(string value) => WriteAscii(_stream, value);

    private static void WriteAscii(Stream stream, string value)
    {
        var bytes = Encoding.ASCII.GetBytes(value);
        stream.Write(bytes, 0, bytes.Length);
    }

    private static void WritePdfString(Stream stream, string value)
    {
        stream.WriteByte((byte)'(');
        foreach (var character in value)
        {
            var encoded = EncodeWinAnsi(character);
            if (encoded is (byte)'(' or (byte)')' or (byte)'\\') stream.WriteByte((byte)'\\');
            stream.WriteByte(encoded);
        }

        stream.WriteByte((byte)')');
    }

    private static byte EncodeWinAnsi(char character) => character switch
    {
        >= '\u0000' and <= '\u007F' => (byte)character,
        '\u20AC' => 0x80,
        '\u201A' => 0x82,
        '\u0192' => 0x83,
        '\u201E' => 0x84,
        '\u2026' => 0x85,
        '\u2020' => 0x86,
        '\u2021' => 0x87,
        '\u02C6' => 0x88,
        '\u2030' => 0x89,
        '\u0160' => 0x8A,
        '\u2039' => 0x8B,
        '\u0152' => 0x8C,
        '\u017D' => 0x8E,
        '\u2018' => 0x91,
        '\u2019' => 0x92,
        '\u201C' => 0x93,
        '\u201D' => 0x94,
        '\u2022' => 0x95,
        '\u2013' => 0x96,
        '\u2014' => 0x97,
        '\u02DC' => 0x98,
        '\u2122' => 0x99,
        '\u0161' => 0x9A,
        '\u203A' => 0x9B,
        '\u0153' => 0x9C,
        '\u017E' => 0x9E,
        '\u0178' => 0x9F,
        >= '\u00A0' and <= '\u00FF' => (byte)character,
        _ => (byte)'?'
    };

    private static string PdfNumber(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    private readonly record struct ObjectNumber(int Value);
}
