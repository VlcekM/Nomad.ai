using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Threading;
using Nomad.App.Controls;

namespace Nomad.App.Markdown;

/// <summary>
/// Renders markdown text as a stack of block controls. While streaming, the text is re-parsed on
/// every update (coalesced to ~25 fps) but only blocks whose source changed are rebuilt — in
/// practice the last one — so earlier blocks keep their controls, selection and layout.
/// </summary>
public sealed class MarkdownView : StackPanel
{
    public static readonly StyledProperty<string?> MarkdownProperty =
        AvaloniaProperty.Register<MarkdownView, string?>(nameof(Markdown));

    public static readonly StyledProperty<bool> IsStreamingProperty =
        AvaloniaProperty.Register<MarkdownView, bool>(nameof(IsStreaming));

    private readonly List<string> _sources = [];
    private DispatcherTimer? _timer;
    private bool _dirty;
    private MarkdownStyle? _renderedStyle;

    public MarkdownView()
    {
        Spacing = MarkdownStyle.Current.ParagraphSpacing;
    }

    public string? Markdown
    {
        get => GetValue(MarkdownProperty);
        set => SetValue(MarkdownProperty, value);
    }

    public bool IsStreaming
    {
        get => GetValue(IsStreamingProperty);
        set => SetValue(IsStreamingProperty, value);
    }

    /// <summary>Re-renders every view after a text size / font change.</summary>
    public static event Action? StyleChanged;

    public static void NotifyStyleChanged() => StyleChanged?.Invoke();

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        StyleChanged += OnStyleChanged;
        if (!ReferenceEquals(_renderedStyle, MarkdownStyle.Current))
        {
            OnStyleChanged();
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        StyleChanged -= OnStyleChanged;
        _timer?.Stop();
        FinishFades();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == MarkdownProperty)
        {
            Schedule();
        }
        else if (change.Property == IsStreamingProperty && !IsStreaming)
        {
            Render();
        }
    }

    private void OnStyleChanged()
    {
        Spacing = MarkdownStyle.Current.ParagraphSpacing;
        FinishFades();
        Children.Clear();
        _sources.Clear();
        Render();
    }

    private void Schedule()
    {
        if (!IsStreaming)
        {
            Render();
            return;
        }

        _dirty = true;
        if (_timer is null)
        {
            // Background priority keeps user input and window messages responsive even when a large block re-renders.
            _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(40), DispatcherPriority.Background, (_, _) =>
            {
                _timer!.Stop();
                if (_dirty)
                {
                    Render();
                }
            });
        }

        if (!_timer.IsEnabled)
        {
            _timer.Start();
        }
    }

    private void Render()
    {
        _dirty = false;
        _renderedStyle = MarkdownStyle.Current;
        var text = Markdown ?? "";
        var document = Markdig.Markdown.Parse(text, MarkdownRenderer.Pipeline);
        var count = document.Count;
        var index = 0;
        foreach (var block in document)
        {
            // Defer syntax highlighting on the actively streaming last block so a growing code block does not rebuild
            // thousands of tokenized runs 25 times a second; it highlights once closed or when streaming ends.
            var highlight = !IsStreaming || index < count - 1;
            var span = block.Span;
            var source = (span.Start >= 0 && span.End < text.Length && span.Length > 0
                ? text.Substring(span.Start, span.Length)
                : block.GetType().Name + index) + (highlight ? "" : "\0plain");

            if (index < _sources.Count && _sources[index] == source && index < Children.Count)
            {
                index++;
                continue;
            }

            var control = MarkdownRenderer.RenderBlock(block, isFirst: index == 0, highlight: highlight);
            if (IsStreaming)
            {
                try
                {
                    FadeIn(index, index < Children.Count ? Children[index] : null, control);
                }
                catch (Exception)
                {
                    // Cosmetic only: an odd block simply appears without the fade.
                }
            }
            else
            {
                _fades.RemoveAll(f => f.Block == index);
            }

            if (index < Children.Count)
            {
                Children[index] = control;
                _sources[index] = source;
            }
            else
            {
                Children.Add(control);
                _sources.Add(source);
            }

            index++;
        }

        while (Children.Count > index)
        {
            Children.RemoveAt(Children.Count - 1);
        }

        while (_sources.Count > index)
        {
            _sources.RemoveAt(_sources.Count - 1);
        }
    }

    // ------------------------------------------------------------------ streaming fade-in

    private static readonly TimeSpan FadeTime = TimeSpan.FromMilliseconds(160);

    /// <summary>A stretch of new text in one block (visible-character offsets) and when it arrived.</summary>
    private sealed class Fade(int block, int start, int end)
    {
        public int Block { get; } = block;
        public int Start { get; } = start;
        public int End { get; } = end;
        public DateTime Born { get; } = DateTime.UtcNow;
        public Dictionary<Color, SolidColorBrush> Brushes { get; } = [];
        public HashSet<TextBlock> Targets { get; } = [];

        public double Progress => Math.Clamp((DateTime.UtcNow - Born) / FadeTime, 0, 1);
        public double Alpha => 1 - Math.Pow(1 - Progress, 3);
    }

    /// <summary>
    /// How often the fallback steps the fades when render frames don't come.
    /// </summary>
    private static readonly TimeSpan FadeFallbackTime = TimeSpan.FromMilliseconds(250);

    private readonly List<Fade> _fades = [];
    private FrameLoop? _fadeFrames;

    /// <summary>
    /// Steps the fades a few times a second beside the frames. New text starts transparent, so a frame callback that never
    /// arrives (seen on Linux until the window is switched away and back) would otherwise leave it invisible.
    /// </summary>
    private DispatcherTimer? _fadeFallback;

    /// <summary>
    /// The rebuilt block replaces <paramref name="old"/>: the text past the old one's length is new
    /// and fades in. Fades still running from earlier rebuilds of the block are carried over, since
    /// each rebuild makes fresh runs.
    /// </summary>
    private void FadeIn(int block, Control? old, Control rebuilt)
    {
        var before = old is null ? 0 : Runs(old).Sum(r => r.Run.Text?.Length ?? 0);
        var after = Runs(rebuilt).Sum(r => r.Run.Text?.Length ?? 0);

        // Finalize any expired fades before dropping them so earlier blocks' brushes are never abandoned transparent.
        StepFades();

        if (after > before)
        {
            _fades.Add(new Fade(block, before, after));
        }

        foreach (var fade in _fades.Where(f => f.Block == block))
        {
            fade.Targets.Clear();
            Apply(rebuilt, fade);
        }

        if (_fades.Count > 0)
        {
            (_fadeFrames ??= new FrameLoop(this, _ => StepFades())).Update(true);
            (_fadeFallback ??= new DispatcherTimer(FadeFallbackTime, DispatcherPriority.Background, (_, _) => StepFades())).Start();
        }
    }

    private void StepFades()
    {
        foreach (var fade in _fades)
        {
            var done = fade.Progress >= 1;
            var alpha = done ? 1.0 : fade.Alpha;
            foreach (var (color, brush) in fade.Brushes)
            {
                brush.Color = done ? color : Color.FromArgb((byte)Math.Round(color.A * alpha), color.R, color.G, color.B);
            }

            // Mutating SolidColorBrush.Color on a Run.Foreground does not notify its host TextBlock to repaint.
            foreach (var target in fade.Targets)
            {
                target.InvalidateVisual();
            }
        }

        _fades.RemoveAll(f => f.Progress >= 1);
        if (_fades.Count == 0)
        {
            _fadeFrames?.Stop();
            _fadeFallback?.Stop();
        }
    }

    private void FinishFades()
    {
        foreach (var fade in _fades)
        {
            foreach (var (color, brush) in fade.Brushes)
            {
                brush.Color = color;
            }

            foreach (var target in fade.Targets)
            {
                target.InvalidateVisual();
            }
        }

        _fades.Clear();
        _fadeFrames?.Stop();
        _fadeFallback?.Stop();
    }

    /// <summary>Splits the runs at the fade's edges and gives the part inside it a fading brush.</summary>
    private static void Apply(Control control, Fade fade)
    {
        var all = Runs(control).ToList();
        if (all.Count > 200)
        {
            return; // Avoid quadratic inline-collection churn on huge blocks.
        }

        // Compute edits forward so character offsets stay exact, then apply per owner from back to front so indices stay valid.
        var alpha = fade.Alpha;
        var offset = 0;
        var edits = new List<(InlineCollection Owner, Run Run, List<Run> Parts)>();
        foreach (var (run, owner, foreground, host) in all)
        {
            var text = run.Text ?? "";
            var start = offset;
            offset += text.Length;
            var from = Math.Max(fade.Start, start) - start;
            var to = Math.Min(fade.End, offset) - start;
            if (from >= to || foreground is not ISolidColorBrush solid)
            {
                continue;
            }

            if (!fade.Brushes.TryGetValue(solid.Color, out var brush))
            {
                brush = new SolidColorBrush(Color.FromArgb((byte)Math.Round(solid.Color.A * alpha), solid.Color.R, solid.Color.G, solid.Color.B));
                fade.Brushes[solid.Color] = brush;
            }

            fade.Targets.Add(host);

            var parts = new List<Run>(3);
            if (from > 0)
            {
                parts.Add(Copy(run, text[..from], run.Foreground));
            }

            parts.Add(Copy(run, text[from..to], brush));
            if (to < text.Length)
            {
                parts.Add(Copy(run, text[to..], run.Foreground));
            }

            edits.Add((owner, run, parts));
        }

        for (var i = edits.Count - 1; i >= 0; i--)
        {
            var (owner, run, parts) = edits[i];
            var index = owner.IndexOf(run);
            if (index >= 0)
            {
                owner.RemoveAt(index);
                owner.InsertRange(index, parts);
            }
        }
    }

    private static Run Copy(Run run, string text, IBrush? foreground)
    {
        var copy = new Run(text) { Foreground = foreground };
        foreach (var property in new AvaloniaProperty[] { Inline.TextDecorationsProperty, TextElement.FontWeightProperty, TextElement.FontStyleProperty, TextElement.FontFamilyProperty, TextElement.FontSizeProperty, TextElement.BackgroundProperty })
        {
            if (run.IsSet(property))
            {
                copy.SetValue(property, run.GetValue(property));
            }
        }

        return copy;
    }

    /// <summary>Every run of every text block in a rendered block, in reading order, with its effective colour.</summary>
    private static IEnumerable<(Run Run, InlineCollection Owner, IBrush? Foreground, TextBlock Host)> Runs(Control control)
    {
        if (control is TextBlock { Inlines: { } inlines } textBlock)
        {
            foreach (var run in Walk(inlines, textBlock.Foreground, textBlock))
            {
                yield return run;
            }

            yield break;
        }

        foreach (var child in control.GetLogicalChildren().OfType<Control>())
        {
            foreach (var run in Runs(child))
            {
                yield return run;
            }
        }
    }

    private static IEnumerable<(Run, InlineCollection, IBrush?, TextBlock)> Walk(InlineCollection inlines, IBrush? inherited, TextBlock host)
    {
        foreach (var inline in inlines)
        {
            var foreground = inline.IsSet(TextElement.ForegroundProperty) ? inline.Foreground : inherited;
            switch (inline)
            {
                case Run run:
                    yield return (run, inlines, foreground, host);
                    break;
                case Span span:
                    foreach (var inner in Walk(span.Inlines, foreground, host))
                    {
                        yield return inner;
                    }

                    break;
            }
        }
    }
}
