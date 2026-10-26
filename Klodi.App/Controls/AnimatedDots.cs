using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace Klodi.App.Controls;

/// <summary>"", ".", "..", "..." cycling while visible and <see cref="IsRunning"/>.</summary>
public sealed class AnimatedDots : TextBlock
{
    public static readonly StyledProperty<bool> IsRunningProperty =
        AvaloniaProperty.Register<AnimatedDots, bool>(nameof(IsRunning), true);

    private DispatcherTimer? _timer;
    private int _step;

    public bool IsRunning
    {
        get => GetValue(IsRunningProperty);
        set => SetValue(IsRunningProperty, value);
    }

    protected override Type StyleKeyOverride => typeof(TextBlock);

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Update();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _timer?.Stop();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsRunningProperty || change.Property == IsVisibleProperty)
        {
            Update();
        }
    }

    private void Update()
    {
        if (IsRunning && IsVisible && VisualRoot is not null)
        {
            _timer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(400), DispatcherPriority.Background, (_, _) =>
            {
                _step = (_step + 1) % 4;
                Text = new string('.', _step);
            });
            _timer.Start();
        }
        else
        {
            _timer?.Stop();
            Text = "";
        }
    }
}
