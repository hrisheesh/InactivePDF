using InactivePDF.Domain.Models;

namespace InactivePDF.Infrastructure.Resources;

public static class WatermarkAssetResolver
{
    public static WatermarkOptions Resolve(WatermarkOptions options, string assetRoot)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(assetRoot);
        if (options.Kind != WatermarkKind.Image || string.IsNullOrWhiteSpace(options.ImagePath)) return options;
        var assetName = options.ImagePath.Trim();
        if (!string.Equals(Path.GetFileName(assetName), assetName, StringComparison.Ordinal) || assetName.Contains('/') || assetName.Contains('\\') || !IsSupportedAsset(assetName))
            throw new UnauthorizedAccessException("The watermark asset must be a supported file in the configured asset directory.");
        var assetPath = WorkspacePathSecurity.EnsureSafeChild(assetRoot, Path.Combine(assetRoot, assetName));
        if (!File.Exists(assetPath) || File.GetAttributes(assetPath).HasFlag(FileAttributes.ReparsePoint))
            throw new UnauthorizedAccessException("The watermark asset must be a regular file inside the configured asset directory.");
        return options with { ImagePath = assetPath };
    }

    private static bool IsSupportedAsset(string path) => Path.GetExtension(path).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" or ".bmp";
}
