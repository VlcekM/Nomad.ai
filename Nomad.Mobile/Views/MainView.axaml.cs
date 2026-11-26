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
                _keyboardRect = _keyboard.State == InputPaneState.Open ? _keyboard.OccludedRect : null;
                FitAbove();
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

        base.OnDetachedFromVisualTree(e);
    }

    private void OnKeyboardChanged(object? sender, InputPaneStateEventArgs e)
    {
        _keyboardRect = e.NewState == InputPaneState.Open ? e.EndRect : null;
        FitAbove();

        // Some keyboards report where they start rather than where they end the first time they open: asked again once
        // they've settled.
        DispatcherTimer.RunOnce(() =>
        {
            if (_keyboard is not null)
            {
                _keyboardRect = _keyboard.State == InputPaneState.Open ? _keyboard.OccludedRect : null;
                FitAbove();
            }
        }, TimeSpan.FromMilliseconds(400));
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
