using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Platform;
using Avalonia.Interactivity;
using Nomad.Mobile.ViewModels;

namespace Nomad.Mobile.Views;

public partial class MainView : UserControl
{
    private TopLevel? _top;
    private IInputPane? _keyboard;

    public MainView() => InitializeComponent();

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
                FitAbove(_keyboard.State, _keyboard.OccludedRect);
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

    private void OnKeyboardChanged(object? sender, InputPaneStateEventArgs e) => FitAbove(e.NewState, e.EndRect);

    /// <summary>
    /// The on-screen keyboard covers the bottom of the window (Android 15 and later no longer shrink the app for it): the
    /// pages end above it, so the message box and what is typed stay in sight. The view's own padding (Android's safe area,
    /// set by Avalonia) is left alone; the part of the keyboard below it is already clear of the pages.
    /// </summary>
    private void FitAbove(InputPaneState state, Rect keyboard)
    {
        var covered = state == InputPaneState.Open ? (_top?.ClientSize.Height ?? Bounds.Height) - keyboard.Top - Padding.Bottom : 0;
        Pages.Margin = new Thickness(0, 0, 0, Math.Max(0, covered));
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
