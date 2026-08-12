using System.Text;
using ImageMagick;

var root = args.Length > 0 ? Path.GetFullPath(args[0]) : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "InactivePDF", "WatchFolders", "Default");
var source = Path.Combine(root, "TestFixtures");
var destination = Path.Combine(root, "HeavyFixtures");
Directory.CreateDirectory(destination);
var formats = new[] { "bmp", "csv", "doc", "docx", "dot", "gif", "html", "jpg", "odp", "odt", "pdf", "png", "ppt", "pptx", "rtf", "tiff", "txt", "xls", "xlsx" };

foreach (var format in formats)
{
    for (var variant = 1; variant <= 3; variant++)
    {
        var output = Path.Combine(destination, $"heavy-{format}-variant-{variant:D2}.{format}");
        if (format is "bmp" or "gif" or "jpg" or "png" or "tiff")
        {
            CreateHeavyImage(output, format, variant);
        }
        else
        {
            var basePath = Path.Combine(source, BaseName(format));
            CreateHeavyDocument(basePath, output, format, TargetBytes(format, variant));
        }
        Console.WriteLine($"{Path.GetFileName(output)} {new FileInfo(output).Length / (1024d * 1024d):F1} MB");
    }
}

static string BaseName(string format) => format switch
{
    "bmp" => "sample.bmp",
    "csv" => "sample.csv",
    "doc" => "sample.doc",
    "docx" => "sample.docx",
    "dot" => "sample.dot",
    "gif" => "sample.gif",
    "html" => "sample.html",
    "jpg" => "sample.jpg",
    "odp" => "sample.odp",
    "odt" => "sample.odt",
    "pdf" => "sample.pdf",
    "png" => "sample.png",
    "ppt" => "sample.ppt",
    "pptx" => "sample.pptx",
    "rtf" => "sample.rtf",
    "tiff" => "sample.tiff",
    "txt" => "sample.txt",
    "xls" => "sample.xls",
    "xlsx" => "sample.xlsx",
    _ => throw new ArgumentOutOfRangeException(nameof(format))
};

static long TargetBytes(string format, int variant) => (7L + variant) * 1024 * 1024;

static void CopyAndPad(string source, string destination, long targetBytes)
{
    File.Copy(source, destination, overwrite: true);
    using var stream = new FileStream(destination, FileMode.Append, FileAccess.Write, FileShare.Read, 1024 * 1024, FileOptions.SequentialScan);
    var block = new byte[1024 * 1024];
    for (var index = 0; index < block.Length; index++) block[index] = (byte)((index * 31) % 251);
    while (stream.Length < targetBytes)
    {
        var count = (int)Math.Min(block.Length, targetBytes - stream.Length);
        stream.Write(block, 0, count);
    }
}

static void CreateHeavyDocument(string source, string destination, string format, long targetBytes)
{
    switch (format)
    {
        case "html":
            var html = File.ReadAllText(source, Encoding.UTF8);
            var closing = "</body></html>";
            var body = html.Replace(closing, string.Empty, StringComparison.OrdinalIgnoreCase);
            using (var writer = new StreamWriter(destination, false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
            {
                writer.Write(body);
                for (var row = 0; row < 200; row++)
                    writer.Write($"<p>InactivePDF heavy HTML fixture valid content row {row} for stress testing.</p>");
                writer.Write("<!-- InactivePDF stress payload begins ");
                var estimatedBytes = Encoding.UTF8.GetByteCount(body);
                var line = "InactivePDF stress payload data remains valid HTML and is intentionally not rendered. ";
                while (estimatedBytes < targetBytes - closing.Length) { writer.Write(line); estimatedBytes += Encoding.UTF8.GetByteCount(line); }
                writer.Write(" -->");
                writer.Write(closing);
            }
            break;
        case "csv":
            using (var writer = new StreamWriter(destination, false, Encoding.UTF8))
            {
                writer.WriteLine("RecordId,Category,Description,Value");
                var row = 0;
                var estimatedBytes = 40;
                while (estimatedBytes < targetBytes) { var line = $"{row++},HeavyFixture,Valid structured CSV content for stress testing,{row * 17}\n"; writer.Write(line); estimatedBytes += Encoding.UTF8.GetByteCount(line); }
            }
            break;
        case "txt":
            using (var writer = new StreamWriter(destination, false, Encoding.UTF8))
            {
                var row = 0;
                var estimatedBytes = 0;
                while (estimatedBytes < targetBytes) { var line = $"Record {row++}: InactivePDF heavy text fixture with valid content and deterministic stress data.\n"; writer.Write(line); estimatedBytes += Encoding.UTF8.GetByteCount(line); }
            }
            break;
        case "rtf":
            using (var writer = new StreamWriter(destination, false, Encoding.ASCII))
            {
                writer.Write(@"{\rtf1\ansi\deff0{\fonttbl{\f0 Arial;}{\f1 Times New Roman;}{\f2 Consolas;}}\f0\fs24 ");
                for (var row = 0; row < 200; row++) writer.Write($"Record {row}: InactivePDF heavy RTF fixture with valid structured text.\\par ");
                writer.Write(@"{\comment InactivePDF stress payload begins ");
                var estimatedBytes = 1_000;
                var line = "InactivePDF stress payload data remains valid RTF and is intentionally not rendered. ";
                while (estimatedBytes < targetBytes) { writer.Write(line); estimatedBytes += Encoding.ASCII.GetByteCount(line); }
                writer.Write('}');
                writer.Write('}');
            }
            break;
        case "pdf":
            CopyAndPad(source, destination, targetBytes);
            break;
        case "doc":
        case "dot":
        case "xls":
        case "ppt":
            CopyAndPad(source, destination, targetBytes);
            break;
        default:
            CopyAndPadZip(source, destination, targetBytes, format);
            break;
    }
}

static void CopyAndPadZip(string source, string destination, long targetBytes, string format)
{
    var temporary = destination + ".tmp";
    if (File.Exists(temporary)) File.Delete(temporary);
    using (var sourceArchive = System.IO.Compression.ZipFile.OpenRead(source))
    using (var destinationStream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, FileOptions.SequentialScan))
    using (var destinationArchive = new System.IO.Compression.ZipArchive(destinationStream, System.IO.Compression.ZipArchiveMode.Create))
    {
        foreach (var sourceEntry in sourceArchive.Entries)
        {
            var destinationEntry = destinationArchive.CreateEntry(
                sourceEntry.FullName,
                sourceEntry.FullName.Equals("mimetype", StringComparison.Ordinal)
                    ? System.IO.Compression.CompressionLevel.NoCompression
                    : System.IO.Compression.CompressionLevel.Optimal);

            using var input = sourceEntry.Open();
            using var output = destinationEntry.Open();
            if (format is "odt" or "odp" && sourceEntry.FullName.Equals("META-INF/manifest.xml", StringComparison.Ordinal))
            {
                using var reader = new StreamReader(input, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), detectEncodingFromByteOrderMarks: true);
                var manifest = reader.ReadToEnd();
                var updated = manifest.Replace(
                    "</manifest:manifest>",
                    "<manifest:file-entry manifest:media-type=\"application/octet-stream\" manifest:full-path=\"customXml/inactivepdf-heavy-payload.bin\"/></manifest:manifest>",
                    StringComparison.Ordinal);
                using var writer = new StreamWriter(output, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), 64 * 1024, leaveOpen: false);
                writer.Write(updated);
            }
            else
            {
                input.CopyTo(output);
            }
        }

        var payload = destinationArchive.CreateEntry("customXml/inactivepdf-heavy-payload.bin", System.IO.Compression.CompressionLevel.NoCompression);
        using var payloadStream = payload.Open();
        var random = new Random(destination.Length);
        var block = new byte[1024 * 1024];
        while (payloadStream.Position < targetBytes)
        {
            random.NextBytes(block);
            var count = (int)Math.Min(block.Length, targetBytes - payloadStream.Position);
            payloadStream.Write(block, 0, count);
        }
    }

    File.Move(temporary, destination, overwrite: true);
}

static void CreateHeavyImage(string path, string format, int variant)
{
    const int width = 3200;
    const int height = 2600;
    using var image = new MagickImage(MagickColors.Black, width, height);
    image.AddNoise(NoiseType.Random);
    image.Settings.Font = "Arial";
    image.Settings.FontPointsize = 48;
    image.Settings.FillColor = MagickColors.White;
    image.Annotate($"InactivePDF HEAVY FIXTURE | {format.ToUpperInvariant()} | VARIANT {variant} | 3200x2600", Gravity.Northwest);
    image.Settings.FontPointsize = 30;
    image.Settings.FillColor = MagickColors.Gold;
    image.Annotate("Stress test: text, color, noise, compression and long-running conversion", Gravity.Southwest);
    image.Format = format switch
    {
        "bmp" => MagickFormat.Bmp,
        "gif" => MagickFormat.Gif,
        "jpg" => MagickFormat.Jpeg,
        "png" => MagickFormat.Png,
        "tiff" => MagickFormat.Tiff,
        _ => throw new ArgumentOutOfRangeException(nameof(format))
    };
    if (format == "jpg") image.Quality = 92;
    image.Write(path);
    if (new FileInfo(path).Length < 7L * 1024 * 1024) AppendPadding(path, 8L * 1024 * 1024);
}

static void AppendPadding(string path, long targetBytes)
{
    using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read, 1024 * 1024, FileOptions.SequentialScan);
    var block = new byte[1024 * 1024];
    for (var index = 0; index < block.Length; index++) block[index] = (byte)((index * 31) % 251);
    while (stream.Length < targetBytes)
    {
        var count = (int)Math.Min(block.Length, targetBytes - stream.Length);
        stream.Write(block, 0, count);
    }
}
