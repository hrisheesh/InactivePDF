using PdfSharp.Fonts;

namespace InactivePDF.Infrastructure.Rendering;

internal sealed class SystemFontResolver : IFontResolver
{
    private static readonly string[] FontDirectories =
    [
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts)),
        @"C:\Windows\Fonts",
        "/System/Library/Fonts/Supplemental",
        "/Library/Fonts",
        "/usr/share/fonts/truetype/msttcorefonts",
        "/usr/share/fonts/truetype/dejavu"
    ];

    public FontResolverInfo? ResolveTypeface(string familyName, bool isBold, bool isItalic)
    {
        var requested = familyName.Equals("Arial", StringComparison.OrdinalIgnoreCase) ? "Arial" : familyName;
        var suffix = isBold && isItalic ? " Bold Italic" : isBold ? " Bold" : isItalic ? " Italic" : string.Empty;
        var candidates = new[] { $"{requested}{suffix}.ttf", $"{requested}.ttf", "Arial.ttf", "DejaVuSans.ttf" };
        foreach (var directory in FontDirectories.Where(Directory.Exists))
        {
            foreach (var candidate in candidates)
            {
                var path = Path.Combine(directory, candidate);
                if (File.Exists(path)) return new FontResolverInfo(path);
            }
        }

        return null;
    }

    public byte[] GetFont(string faceName) => File.ReadAllBytes(faceName);
}
