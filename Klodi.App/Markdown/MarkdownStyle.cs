using Avalonia.Media;

namespace Klodi.App.Markdown;

/// <summary>Sizes and fonts of rendered transcript text (driven by the Text size and font settings).</summary>
public sealed class MarkdownStyle
{
    public static MarkdownStyle Current { get; set; } = new();

    public double BodySize { get; init; } = 15;

    public double CodeSize { get; init; } = 13;

    public FontFamily BodyFont { get; init; } = new("fonts:Inter#Inter, Segoe UI");

    public FontFamily CodeFont { get; init; } = new("Cascadia Mono, Consolas, Courier New");

    /// <summary>Line height for body text (≈ 1.4).</summary>
    public double LineHeight => Math.Round(BodySize * 1.4);

    public double ParagraphSpacing => Math.Round(BodySize * 0.7);

    public double H1 => Math.Round(BodySize * 20 / 15);

    public double H2 => Math.Round(BodySize * 17 / 15);

    public double H3 => BodySize;

    public static MarkdownStyle From(string textSize, string uiFont, string codeFont)
    {
        var (body, code) = textSize switch
        {
            "Small" => (14.0, 12.0),
            "Large" => (16.0, 14.0),
            _ => (15.0, 13.0),
        };

        return new MarkdownStyle
        {
            BodySize = body,
            CodeSize = code,
            BodyFont = Fonts.Ui(uiFont),
            CodeFont = Fonts.Code(codeFont),
        };
    }
}

/// <summary>Maps font setting names to font families (Inter is bundled).</summary>
public static class Fonts
{
    public static FontFamily Ui(string? name) =>
        string.IsNullOrWhiteSpace(name) || name == "Inter"
            ? new FontFamily("fonts:Inter#Inter, Segoe UI")
            : new FontFamily($"{name}, fonts:Inter#Inter, Segoe UI");

    public static FontFamily Code(string? name) =>
        string.IsNullOrWhiteSpace(name)
            ? new FontFamily("Cascadia Mono, Consolas, Courier New")
            : new FontFamily($"{name}, Cascadia Mono, Consolas, Courier New");
}
