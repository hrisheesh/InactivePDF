using System.Diagnostics;
using System.Text;
using ImageMagick;
using PdfSharp.Pdf;

var root = args.Length > 0 ? Path.GetFullPath(args[0]) : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "InactivePDF", "WatchFolders", "Default");
var fixtures = Path.Combine(root, "TestFixtures");
var input = Path.Combine(root, "Input");
Directory.CreateDirectory(fixtures);
Directory.CreateDirectory(input);

File.WriteAllText(Path.Combine(fixtures, "sample.txt"), "InactivePDF watch-folder text fixture." + Environment.NewLine + "Unicode: café — नमस्ते — שלום.");
File.WriteAllText(Path.Combine(fixtures, "sample.html"), "<!doctype html><html><body><h1>InactivePDF</h1><p>Watch-folder HTML fixture.</p></body></html>");
File.WriteAllText(Path.Combine(fixtures, "sample.rtf"), "{\\rtf1\\ansi\\deff0 {\\fonttbl {\\f0 Arial;}}\\f0\\fs24 InactivePDF RTF fixture.}");
CreateImage(Path.Combine(fixtures, "sample.png"), MagickColors.CornflowerBlue, MagickFormat.Png);
CreateImage(Path.Combine(fixtures, "sample.jpg"), MagickColors.OrangeRed, MagickFormat.Jpeg);
CreateImage(Path.Combine(fixtures, "sample.bmp"), MagickColors.ForestGreen, MagickFormat.Bmp);
CreateImage(Path.Combine(fixtures, "sample.gif"), MagickColors.Goldenrod, MagickFormat.Gif);
CreateImage(Path.Combine(fixtures, "sample.tiff"), MagickColors.Purple, MagickFormat.Tiff);
using (var pdf = new PdfDocument()) { pdf.AddPage(); pdf.Save(Path.Combine(fixtures, "sample.pdf")); }
CreateCsv(Path.Combine(fixtures, "sample.csv"));
CreateDocx(Path.Combine(fixtures, "sample.docx"));
CreateXlsx(Path.Combine(fixtures, "sample.xlsx"));
CreatePptx(Path.Combine(fixtures, "sample.pptx"));
CreateLegacyOfficeFile(Path.Combine(fixtures, "sample.docx"), Path.Combine(fixtures, "sample.odt"), "odt", null);
CreateLegacyOfficeFile(Path.Combine(fixtures, "sample.pptx"), Path.Combine(fixtures, "sample.odp"), "odp", null);
CreateLegacyOfficeFile(Path.Combine(fixtures, "sample.docx"), Path.Combine(fixtures, "sample.doc"), "doc", "MS Word 97");
CreateLegacyOfficeFile(Path.Combine(fixtures, "sample.docx"), Path.Combine(fixtures, "sample.dot"), "doc", "MS Word 97");
CreateLegacyOfficeFile(Path.Combine(fixtures, "sample.xlsx"), Path.Combine(fixtures, "sample.xls"), "xls", "MS Excel 97");
CreateLegacyOfficeFile(Path.Combine(fixtures, "sample.pptx"), Path.Combine(fixtures, "sample.ppt"), "ppt", "MS PowerPoint 97");
CopyFixture("sample.csv", "fixture-data-csv.csv");
CopyFixture("sample.pdf", "fixture-existing-pdf.pdf");
CopyFixture("sample.bmp", "fixture-image-bmp.bmp");
CopyFixture("sample.gif", "fixture-image-gif.gif");
CopyFixture("sample.jpg", "fixture-image-jpeg.jpg");
CopyFixture("sample.png", "fixture-image-png.png");
CopyFixture("sample.tiff", "fixture-image-tiff.tiff");
CopyFixture("sample.doc", "fixture-office-doc.doc");
CopyFixture("sample.docx", "fixture-office-docx.docx");
CopyFixture("sample.odt", "fixture-office-odt.odt");
CopyFixture("sample.dot", "fixture-office-template.dot");
CopyFixture("sample.odp", "fixture-presentation-odp.odp");
CopyFixture("sample.ppt", "fixture-presentation-ppt.ppt");
CopyFixture("sample.pptx", "fixture-presentation-pptx.pptx");
CopyFixture("sample.xls", "fixture-spreadsheet-xls.xls");
CopyFixture("sample.xlsx", "fixture-spreadsheet-xlsx.xlsx");
CopyFixture("sample.rtf", "fixture-text-rtf.rtf");
CopyFixture("sample.txt", "fixture-text-txt.txt");
CopyFixture("sample.html", "fixture-web-html.html");
CopyToInput("sample.txt");
Console.WriteLine($"Created fixtures in {fixtures}");

static void CreateLegacyOfficeFile(string source, string destination, string outputExtension, string? filter)
{
    var executable = Environment.GetEnvironmentVariable("INACTIVEPDF_LIBREOFFICE_PATH") ??
        (OperatingSystem.IsWindows() ? @"C:\Program Files\LibreOffice\program\soffice.exe" : "soffice");
    var temporaryDirectory = Path.Combine(Path.GetTempPath(), $"inactivepdf-fixtures-{Guid.NewGuid():N}");
    var profileDirectory = Path.Combine(temporaryDirectory, "profile");
    Directory.CreateDirectory(profileDirectory);
    try
    {
        var startInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = temporaryDirectory
        };
        startInfo.ArgumentList.Add("--headless");
        startInfo.ArgumentList.Add("--invisible");
        startInfo.ArgumentList.Add("--nodefault");
        startInfo.ArgumentList.Add("--nologo");
        startInfo.ArgumentList.Add("--nolockcheck");
        startInfo.ArgumentList.Add("--norestore");
        startInfo.ArgumentList.Add("--nofirststartwizard");
        startInfo.ArgumentList.Add($"-env:UserInstallation={new Uri(profileDirectory + Path.DirectorySeparatorChar).AbsoluteUri.TrimEnd('/')}");
        startInfo.ArgumentList.Add("--convert-to");
        startInfo.ArgumentList.Add(filter is null ? outputExtension : $"{outputExtension}:{filter}");
        startInfo.ArgumentList.Add("--outdir");
        startInfo.ArgumentList.Add(temporaryDirectory);
        startInfo.ArgumentList.Add(Path.GetFullPath(source));

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("LibreOffice failed to start while creating a legacy fixture.");
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        try
        {
            process.WaitForExitAsync(timeout.Token).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw new TimeoutException($"LibreOffice exceeded two minutes while creating '{Path.GetFileName(destination)}'.");
        }

        var output = standardOutput.GetAwaiter().GetResult().Trim();
        var error = standardError.GetAwaiter().GetResult().Trim();
        var generated = Path.Combine(temporaryDirectory, $"{Path.GetFileNameWithoutExtension(source)}.{outputExtension}");
        if (process.ExitCode != 0 || !File.Exists(generated))
            throw new InvalidOperationException($"LibreOffice could not create the legacy '{outputExtension}' fixture. ExitCode={process.ExitCode} StandardOutput={output} StandardError={error}");

        File.Copy(generated, destination, overwrite: true);
    }
    finally
    {
        try { if (Directory.Exists(temporaryDirectory)) Directory.Delete(temporaryDirectory, recursive: true); } catch { }
    }
}

static void CreateImage(string path, MagickColor color, MagickFormat format)
{
    using var image = new MagickImage(color, 640, 480) { Format = format };
    image.Write(path);
}

void CopyToInput(string name) => File.Copy(Path.Combine(fixtures, name), Path.Combine(input, name), overwrite: true);
void CopyFixture(string source, string destination) => File.Copy(Path.Combine(fixtures, source), Path.Combine(fixtures, destination), overwrite: true);

static void CreateCsv(string path) => File.WriteAllText(path, "Name,Value\nInactivePDF,Watch folder fixture\n");
static void CreateDocx(string path) => CreateZip(path,
    ("[Content_Types].xml", "<?xml version=\"1.0\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/><Override PartName=\"/word/document.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml\"/></Types>"),
    ("_rels/.rels", "<?xml version=\"1.0\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"word/document.xml\"/></Relationships>"),
    ("word/document.xml", "<?xml version=\"1.0\"?><w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body><w:p><w:r><w:t>InactivePDF DOCX fixture.</w:t></w:r></w:p><w:sectPr/></w:body></w:document>"));
static void CreateXlsx(string path) => CreateZip(path,
    ("[Content_Types].xml", "<?xml version=\"1.0\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/><Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/><Override PartName=\"/xl/worksheets/sheet1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/></Types>"),
    ("_rels/.rels", "<?xml version=\"1.0\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/></Relationships>"),
    ("xl/workbook.xml", "<?xml version=\"1.0\"?><workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets><sheet name=\"Sheet1\" sheetId=\"1\" r:id=\"rId1\"/></sheets></workbook>"),
    ("xl/_rels/workbook.xml.rels", "<?xml version=\"1.0\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/></Relationships>"),
    ("xl/worksheets/sheet1.xml", "<?xml version=\"1.0\"?><worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData><row r=\"1\"><c r=\"A1\" t=\"inlineStr\"><is><t>InactivePDF XLSX fixture.</t></is></c></row></sheetData></worksheet>"));
static void CreatePptx(string path) => CreateZip(path,
    ("[Content_Types].xml", "<?xml version=\"1.0\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/><Override PartName=\"/ppt/presentation.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.presentationml.presentation.main+xml\"/><Override PartName=\"/ppt/slides/slide1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.presentationml.slide+xml\"/></Types>"),
    ("_rels/.rels", "<?xml version=\"1.0\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"ppt/presentation.xml\"/></Relationships>"),
    ("ppt/presentation.xml", "<?xml version=\"1.0\"?><p:presentation xmlns:p=\"http://schemas.openxmlformats.org/presentationml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><p:sldIdLst><p:sldId id=\"256\" r:id=\"rId1\"/></p:sldIdLst><p:sldSz cx=\"12192000\" cy=\"6858000\"/></p:presentation>"),
    ("ppt/_rels/presentation.xml.rels", "<?xml version=\"1.0\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/slide\" Target=\"slides/slide1.xml\"/></Relationships>"),
    ("ppt/slides/slide1.xml", "<?xml version=\"1.0\"?><p:sld xmlns:p=\"http://schemas.openxmlformats.org/presentationml/2006/main\"><p:cSld><p:spTree><p:nvGrpSpPr><p:cNvPr id=\"1\" name=\"\"/><p:cNvGrpSpPr/><p:nvPr/></p:nvGrpSpPr><p:grpSpPr/></p:spTree></p:cSld></p:sld>"));
static void CreateZip(string path, params (string Name, string Content)[] entries)
{
    if (File.Exists(path)) File.Delete(path);
    using var archive = System.IO.Compression.ZipFile.Open(path, System.IO.Compression.ZipArchiveMode.Create);
    foreach (var entry in entries)
    {
        var item = archive.CreateEntry(entry.Name, entry.Name == "mimetype" ? System.IO.Compression.CompressionLevel.NoCompression : System.IO.Compression.CompressionLevel.Optimal);
        using var writer = new StreamWriter(item.Open(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.Write(entry.Content);
    }
}
