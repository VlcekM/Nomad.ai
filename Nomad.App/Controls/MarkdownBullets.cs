using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Nomad.App.Controls;

/// <summary>
/// Draws a dot over each list bullet of a <see cref="MarkdownPresenter"/> (which leaves the <c>-</c>, <c>*</c> or
/// <c>+</c> itself transparent): the text keeps its Markdown, the box shows a list.
/// </summary>
public sealed class MarkdownBullets : Control
{
    public static readonly StyledProperty<MarkdownPresenter?> PresenterProperty =
        AvaloniaProperty.Register<MarkdownBullets, MarkdownPresenter?>(nameof(Presenter));

    public static readonly StyledProperty<IBrush?> FillProperty =
        AvaloniaProperty.Register<MarkdownBullets, IBrush?>(nameof(Fill));

    public MarkdownBullets()
    {
        IsHitTestVisible = false;
    }

    public MarkdownPresenter? Presenter
    {
        get => GetValue(PresenterProperty);
        set => SetValue(PresenterProperty, value);
    }

    public IBrush? Fill
    {
        get => GetValue(FillProperty);
        set => SetValue(FillProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == PresenterProperty)
        {
            if (change.GetOldValue<MarkdownPresenter?>() is { } old)
            {
                old.LayoutCreated -= OnLayout;
                old.LayoutUpdated -= OnLayout;
            }

            if (change.GetNewValue<MarkdownPresenter?>() is { } presenter)
            {
                presenter.LayoutCreated += OnLayout;
                presenter.LayoutUpdated += OnLayout;
            }

            InvalidateVisual();
        }
        else if (change.Property == FillProperty)
        {
            InvalidateVisual();
        }
    }

    private void OnLayout(object? sender, EventArgs e) => InvalidateVisual();

    public override void Render(DrawingContext context)
    {
        if (Presenter is not { } presenter || Fill is not { } fill || presenter.Bullets.Count == 0
            || presenter.TranslatePoint(default, this) is not { } origin)
        {
            return;
        }

        var layout = presenter.TextLayout;
        var radius = Math.Max(2, presenter.FontSize * 0.14);
        foreach (var index in presenter.Bullets)
        {
            var glyph = layout.HitTestTextPosition(index);
            var next = layout.HitTestTextPosition(index + 1);
            var width = next.Y == glyph.Y && next.X > glyph.X ? next.X - glyph.X : presenter.FontSize * 0.5;
            var center = origin + new Point(glyph.X + width / 2, glyph.Y + glyph.Height / 2);
            context.DrawEllipse(fill, null, center, radius, radius);
        }
    }
}
