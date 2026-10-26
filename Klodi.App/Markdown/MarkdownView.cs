using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace Klodi.App.Markdown;

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
        var index = 0;
        foreach (var block in document)
        {
            var span = block.Span;
            var source = span.Start >= 0 && span.End < text.Length && span.Length > 0
                ? text.Substring(span.Start, span.Length)
                : block.GetType().Name + index;

            if (index < _sources.Count && _sources[index] == source && index < Children.Count)
            {
                index++;
                continue;
            }

            var control = MarkdownRenderer.RenderBlock(block, isFirst: index == 0);
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
}
