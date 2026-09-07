using InactivePDF.Domain.Models;

namespace InactivePDF.Infrastructure.Resources;

public static class WatermarkAssetResolver
{
    public static WatermarkOptions Resolve(WatermarkOptions options, string assetRoot)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(assetRoot);
        if (options.Kind != WatermarkKind.Image || string.IsNullOrWhiteSpace(options.ImagePath)) return options;
        var assetPath = WorkspacePathSecurity.EnsureSafeChild(assetRoot, Path.Combine(assetRoot, options.ImagePath));
        if (!File.Exists(assetPath) || File.GetAttributes(assetPath).HasFlag(FileAttributes.ReparsePoint))
            throw new UnauthorizedAccessException("The watermark asset must be a regular file inside the configured asset directory.");
        return options with { ImagePath = assetPath };
    }
}
