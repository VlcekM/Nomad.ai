using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Nomad.App.Infrastructure;

namespace Nomad.App.Controls;

/// <summary>
/// The working mark of the desktop app: a 20 px animation per activity (<see cref="ClayPlayer"/>), started fresh
/// each time it is shown. Without the Claude app installed it is the pixel spinner. Animates only while visible.
/// </summary>
public sealed class ClayMark : Control
{
    public static readonly StyledProperty<string> ActivityProperty =
        AvaloniaProperty.Register<ClayMark, string>(nameof(Activity), "default");

    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        AvaloniaProperty.Register<ClayMark, IBrush?>(nameof(Foreground));

    private static readonly TimeSpan SpinnerPeriod = TimeSpan.FromMilliseconds(1100);

    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private DispatcherTimer? _timer;
    private ClayPlayer? _player;

    static ClayMark()
    {
        AffectsRender<ClayMark>(ForegroundProperty);
    }

    public ClayMark()
    {
        RenderOptions.SetBitmapInterpolationMode(this, Avalonia.Media.Imaging.BitmapInterpolationMode.HighQuality);
    }

    /// <summary>"think", "read", "search", "code", "write", "compose", "blocked" or "default".</summary>
    public string Activity
    {
        get => GetValue(ActivityProperty);
        set => SetValue(ActivityProperty, value);
    }

    /// <summary>The pixel spinner's colour, when the Claude app's sheets are not there.</summary>
    public IBrush? Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize) => new(20, 20);

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        WorkingMarkSheets.Loaded += OnSheetsLoaded;
        Restart();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        WorkingMarkSheets.Loaded -= OnSheetsLoaded;
        _timer?.Stop();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsVisibleProperty)
        {
            Restart();
        }
        else if (change.Property == ActivityProperty)
        {
            _player?.SetActivity(Activity);
        }
    }

    private void OnSheetsLoaded() => Restart();

    // Shown again: the activity comes in from the start.
    private void Restart()
    {
        var running = IsVisible && VisualRoot is not null;
        _player = running && WorkingMarkSheets.Current is { } sheets ? new ClayPlayer(sheets, Activity, Now) : null;
        if (running)
        {
            _timer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Render, (_, _) => InvalidateVisual());
            _timer.Start();
        }
        else
        {
            _timer?.Stop();
        }
    }

    private double Now => _clock.Elapsed.TotalMilliseconds;

    public override void Render(DrawingContext context)
    {
        var side = Math.Min(Bounds.Width, Bounds.Height);
        var rect = new Rect((Bounds.Width - side) / 2, (Bounds.Height - side) / 2, side, side);
        if (_player is { } player)
        {
            player.Advance(Now);
            player.Draw(context, rect, Now);
        }
        else if (Foreground is ISolidColorBrush brush)
        {
            // The pixel spinner, 14 px in the middle.
            using (context.PushTransform(Matrix.CreateTranslation(rect.X + ((side - 14) / 2), rect.Y + ((side - 14) / 2))))
            {
                PixelSpinner.Draw(context, new Size(14, 14), brush.Color, _clock.Elapsed / SpinnerPeriod % 1, 1);
            }
        }
    }
}
