using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Platform;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Nomad.Mobile.ViewModels;

namespace Nomad.Mobile.Views;

public partial class MainView : UserControl
{
    private TopLevel? _top;
    private IInputPane? _keyboard;
    private IInsetsManager? _insets;

    // The keyboard's last known place, in the window's coordinates; null while it is closed.
    private Rect? _keyboardRect;

    public MainView()
    {
        InitializeComponent();

        // A phone that does resize the app for the keyboard does it after telling about the keyboard: measured again then.
        SizeChanged += (_, _) => FitAbove();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _top = TopLevel.GetTopLevel(this);
        if (_top is not null)
        {
            _top.BackRequested += OnBackRequested;
            _keyboard = _top.InputPane;
            if (_keyboard is not null)
            {
                _keyboard.StateChanged += OnKeyboardChanged;
                MeasureKeyboard();
            }

            // Android tells the app whenever the space its bars and keyboard take changes, whether or not the keyboard's own
            // report came: measured again each time, so a missed or wrong report can't leave a gap where the keyboard was.
            _insets = _top.InsetsManager;
            if (_insets is not null)
            {
                _insets.SafeAreaChanged += OnSafeAreaChanged;
            }
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_top is not null)
        {
            _top.BackRequested -= OnBackRequested;
        }

        if (_keyboard is not null)
        {
            _keyboard.StateChanged -= OnKeyboardChanged;
            _keyboard = null;
        }

        if (_insets is not null)
        {
            _insets.SafeAreaChanged -= OnSafeAreaChanged;
            _insets = null;
        }

        base.OnDetachedFromVisualTree(e);
    }

    private void OnKeyboardChanged(object? sender, InputPaneStateEventArgs e)
    {
        // Taken at its word for now, so the pages move with it...
        _keyboardRect = e.NewState == InputPaneState.Open && e.EndRect.Height > 0 ? e.EndRect : null;
        FitAbove();

        // ...then measured once it has settled. On Android 12 and later the report comes as the keyboard starts to move and
        // can carry the state from before: a keyboard closing can say it is open, at full height.
        var settled = e.AnimationDuration + TimeSpan.FromMilliseconds(120);
        DispatcherTimer.RunOnce(MeasureKeyboard, settled);
        if (settled < TimeSpan.FromMilliseconds(400))
        {
            DispatcherTimer.RunOnce(MeasureKeyboard, TimeSpan.FromMilliseconds(400));
        }
    }

    private void OnSafeAreaChanged(object? sender, SafeAreaChangedArgs e)
    {
        MeasureKeyboard();
        Dispatcher.UIThread.Post(MeasureKeyboard, DispatcherPriority.Background); // Once Android has finished handing them out.
    }

    /// <summary>
    /// Where the keyboard is now, from the space Android says it takes (none when it is closed), not from the last report.
    /// </summary>
    private void MeasureKeyboard()
    {
        if (_keyboard is null)
        {
            return;
        }

        _keyboardRect = _keyboard.OccludedRect is { Height: > 0 } rect ? rect : null;
        FitAbove();
    }

    /// <summary>
    /// The on-screen keyboard covers the bottom of the window (Android 15 and later no longer shrink the app for it): the
    /// pages end above it, so the message box and what is typed stay in sight. Only the part of the keyboard that actually
    /// overlaps the pages counts: on a phone that shrinks the app itself there is none, and the safe area at the bottom
    /// (the view's padding, set by Avalonia) is already clear of them.
    /// </summary>
    private void FitAbove()
    {
        var covered = 0.0;
        if (_keyboardRect is { } keyboard && _top is not null
            && this.TranslatePoint(new Point(0, Bounds.Height - Padding.Bottom), _top) is { } pagesBottom)
        {
            covered = pagesBottom.Y - keyboard.Top;
        }

        var margin = new Thickness(0, 0, 0, Math.Max(0, covered));
        if (Pages.Margin != margin)
        {
            Pages.Margin = margin;
        }
    }

    /// <summary>Android's back button or gesture: a chat or Settings goes back to the chats; from the chats it leaves the app.</summary>
    private void OnBackRequested(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel main && main.GoBack())
        {
            e.Handled = true;
        }
    }
}
