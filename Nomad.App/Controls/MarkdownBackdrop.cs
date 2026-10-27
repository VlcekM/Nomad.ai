using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Nomad.Core.Text;

namespace Nomad.App.Controls;

/// <summary>
/// Drawn under a <see cref="MarkdownPresenter"/>'s text: a pill behind each <c>`code`</c> span and a panel behind each
/// fenced code block, as the transcript draws them. The text over them stays the text box's own, editable as ever.
/// </summary>
public sealed class MarkdownBackdrop : Control
{
    public static readonly StyledProperty<MarkdownPresenter?> PresenterProperty =
        AvaloniaProperty.Register<MarkdownBackdrop, MarkdownPresenter?>(nameof(Presenter));

    public static readonly StyledProperty<IBrush?> PillBrushProperty =
        AvaloniaProperty.Register<MarkdownBackdrop, IBrush?>(nameof(PillBrush));

    public static readonly StyledProperty<IBrush?> PillBorderBrushProperty =
        AvaloniaProperty.Register<MarkdownBackdrop, IBrush?>(nameof(PillBorderBrush));

    public static readonly StyledProperty<IBrush?> BlockBrushProperty =
        AvaloniaProperty.Register<MarkdownBackdrop, IBrush?>(nameof(BlockBrush));

    public static readonly StyledProperty<IBrush?> BlockBorderBrushProperty =
        AvaloniaProperty.Register<MarkdownBackdrop, IBrush?>(nameof(BlockBorderBrush));

    static MarkdownBackdrop()
    {
        AffectsRender<MarkdownBackdrop>(PillBrushProperty, PillBorderBrushProperty, BlockBrushProperty, BlockBorderBrushProperty);
    }

    public MarkdownBackdrop()
    {
        IsHitTestVisible = false;
    }

    public MarkdownPresenter? Presenter
    {
        get => GetValue(PresenterProperty);
        set => SetValue(PresenterProperty, value);
    }

    public IBrush? PillBrush
    {
        get => GetValue(PillBrushProperty);
        set => SetValue(PillBrushProperty, value);
    }

    public IBrush? PillBorderBrush
    {
        get => GetValue(PillBorderBrushProperty);
        set => SetValue(PillBorderBrushProperty, value);
    }

    public IBrush? BlockBrush
    {
        get => GetValue(BlockBrushProperty);
        set => SetValue(BlockBrushProperty, value);
    }

    public IBrush? BlockBorderBrush
    {
        get => GetValue(BlockBorderBrushProperty);
        set => SetValue(BlockBorderBrushProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == PresenterProperty)
        {
            if (change.GetOldValue<MarkdownPresenter?>() is { } old)
            {
                old.LayoutChanged -= OnLayout;
            }

            if (change.GetNewValue<MarkdownPresenter?>() is { } presenter)
            {
                presenter.LayoutChanged += OnLayout;
            }

            InvalidateVisual();
        }
    }

    private void OnLayout(object? sender, EventArgs e) => InvalidateVisual();

    public override void Render(DrawingContext context)
    {
        if (Presenter is not { } presenter || presenter.TranslatePoint(default, this) is not { } origin)
        {
            return;
        }

        // Read before the spans: the layout is made on first use, and the spans with it.
        var layout = presenter.TextLayout;
        var spans = presenter.Spans;
        var blockPen = BlockBorderBrush is { } blockBorder ? new Pen(blockBorder, 1) : null;
        var pillPen = PillBorderBrush is { } pillBorder ? new Pen(pillBorder, 1) : null;
        var pillHeight = Math.Round(presenter.FontSize * 0.92 + 6);

        for (var i = 0; i < spans.Count; i++)
        {
            var style = spans[i].Style;
            if (style.HasFlag(SpanStyle.CodeBlock))
            {
                // The block's spans run on without a gap (line breaks included): one panel, the box's full width.
                var start = spans[i].Start;
                var end = start + spans[i].Length;
                while (i + 1 < spans.Count && spans[i + 1].Style.HasFlag(SpanStyle.CodeBlock) && spans[i + 1].Start == end)
                {
                    i++;
                    end = spans[i].Start + spans[i].Length;
                }

                var top = double.MaxValue;
                var bottom = double.MinValue;
                foreach (var rect in layout.HitTestTextRange(start, end - start))
                {
                    top = Math.Min(top, rect.Top);
                    bottom = Math.Max(bottom, rect.Bottom);
                }

                if (top < bottom)
                {
                    var panel = new Rect(0, origin.Y + top, Bounds.Width, bottom - top);
                    context.DrawRectangle(BlockBrush, blockPen, new RoundedRect(panel.Deflate(0.5), 6));
                }
            }
            else if (style == SpanStyle.Code)
            {
                // The code itself (its backticks, on either side, are hidden and make room for the pill's edges).
                foreach (var rect in layout.HitTestTextRange(spans[i].Start, spans[i].Length))
                {
                    var y = rect.Y + ((rect.Height - pillHeight) / 2) + 1;
                    var pill = new Rect(rect.X - 3, y, rect.Width + 6, pillHeight).Translate(origin);
                    context.DrawRectangle(PillBrush, pillPen, new RoundedRect(pill.Deflate(0.5), 4));
                }
            }
        }
    }
}
