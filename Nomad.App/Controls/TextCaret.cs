using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Media;
using Avalonia.Threading;

namespace Nomad.App.Controls;

/// <summary>
/// The text box caret, drawn over a <see cref="TextPresenter"/> whose own caret is transparent: Avalonia's sits on
/// the glyph edge (a pixel left of it at the end of a line), this one 1.5 px to the right of it, in
/// <see cref="CaretBrush"/>. It blinks only while its text box has focus and nothing is selected.
/// </summary>
public sealed class TextCaret : Control
{
    public static readonly StyledProperty<TextPresenter?> PresenterProperty =
        AvaloniaProperty.Register<TextCaret, TextPresenter?>(nameof(Presenter));

    public static readonly StyledProperty<IBrush?> CaretBrushProperty =
        AvaloniaProperty.Register<TextCaret, IBrush?>(nameof(CaretBrush), Brushes.White);

    private const double Offset = 1.5;
    private DispatcherTimer? _timer;
    private TextBox? _box;
    private bool _on;

    public TextCaret()
    {
        IsHitTestVisible = false;
    }

    public TextPresenter? Presenter
    {
        get => GetValue(PresenterProperty);
        set => SetValue(PresenterProperty, value);
    }

    public IBrush? CaretBrush
    {
        get => GetValue(CaretBrushProperty);
        set => SetValue(CaretBrushProperty, value);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _box = TemplatedParent as TextBox;
        if (_box is not null)
        {
            _box.GotFocus += OnFocusChanged;
            _box.LostFocus += OnFocusChanged;
            _box.PropertyChanged += OnBoxPropertyChanged;
        }

        Attach(Presenter);
        Restart();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (_box is not null)
        {
            _box.GotFocus -= OnFocusChanged;
            _box.LostFocus -= OnFocusChanged;
            _box.PropertyChanged -= OnBoxPropertyChanged;
            _box = null;
        }

        Detach(Presenter);
        _timer?.Stop();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == PresenterProperty && VisualRoot is not null)
        {
            Detach(change.GetOldValue<TextPresenter?>());
            Attach(change.GetNewValue<TextPresenter?>());
            Restart();
        }
        else if (change.Property == CaretBrushProperty)
        {
            InvalidateVisual();
        }
    }

    private void Attach(TextPresenter? presenter)
    {
        if (presenter is not null)
        {
            presenter.LayoutUpdated += OnPresenterLayoutUpdated;
        }
    }

    private void Detach(TextPresenter? presenter)
    {
        if (presenter is not null)
        {
            presenter.LayoutUpdated -= OnPresenterLayoutUpdated;
        }
    }

    private void OnFocusChanged(object? sender, EventArgs e) => Restart();

    private void OnPresenterLayoutUpdated(object? sender, EventArgs e) => InvalidateVisual();

    private void OnBoxPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        // Typing or moving the caret shows it solid and starts the blink over, as the stock caret does.
        if (e.Property == TextBox.CaretIndexProperty || e.Property == TextBox.TextProperty
            || e.Property == TextBox.SelectionStartProperty || e.Property == TextBox.SelectionEndProperty)
        {
            Restart();
        }
    }

    private bool IsActive => _box is { IsFocused: true, IsReadOnly: false } box && box.SelectionStart == box.SelectionEnd;

    private void Restart()
    {
        if (IsActive && VisualRoot is not null)
        {
            var interval = _box!.CaretBlinkInterval;
            _timer ??= new DispatcherTimer(DispatcherPriority.Normal);
            _timer.Interval = interval > TimeSpan.Zero ? interval : TimeSpan.FromMilliseconds(500);
            _timer.Tick -= OnTick;
            _timer.Tick += OnTick;
            _timer.Stop();
            _timer.Start();
            _on = true;
        }
        else
        {
            _timer?.Stop();
            _on = false;
        }

        InvalidateVisual();
    }

    private void OnTick(object? sender, EventArgs e)
    {
        _on = !_on;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        if (!_on || !IsActive || Presenter is not { } presenter || CaretBrush is not { } brush
            || presenter.TranslatePoint(default, this) is not { } origin)
        {
            return;
        }

        var index = Math.Clamp(_box!.CaretIndex, 0, presenter.Text?.Length ?? 0);
        var rect = presenter.TextLayout.HitTestTextPosition(index);
        var x = Math.Floor(origin.X + rect.X) + Offset;
        var top = Math.Floor(origin.Y + rect.Y) + 0.5;
        var bottom = Math.Ceiling(origin.Y + rect.Bottom) - 0.5;
        context.DrawLine(new Pen(brush), new Point(x, top), new Point(x, bottom));
    }
}
