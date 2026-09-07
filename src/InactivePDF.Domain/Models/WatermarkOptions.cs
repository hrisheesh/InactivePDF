namespace InactivePDF.Domain.Models;

public enum WatermarkKind { Text, Image }
public enum WatermarkPosition { TopLeft, TopCenter, TopRight, CenterLeft, Center, CenterRight, BottomLeft, BottomCenter, BottomRight }
public enum WatermarkLayer { Behind, Over }

public sealed record WatermarkOptions(
    WatermarkKind Kind = WatermarkKind.Text,
    string Text = "",
    string? ImagePath = null,
    string FontFamily = "Arial",
    double FontSize = 36,
    string Color = "#808080",
    double Opacity = 0.2,
    double Rotation = 0,
    WatermarkPosition Position = WatermarkPosition.Center,
    double OffsetX = 0,
    double OffsetY = 0,
    WatermarkLayer Layer = WatermarkLayer.Over,
    string Pages = "all",
    bool Tile = false,
    double? Width = null,
    double? Height = null,
    string? Header = null,
    string? Footer = null,
    string PageNumberFormat = "{page}/{pages}");
