using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Nomad.Mobile.ViewModels;

namespace Nomad.Mobile.Views;

public partial class MainView : UserControl
{
    private TopLevel? _top;

    public MainView() => InitializeComponent();

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _top = TopLevel.GetTopLevel(this);
        if (_top is not null)
        {
            _top.BackRequested += OnBackRequested;
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_top is not null)
        {
            _top.BackRequested -= OnBackRequested;
        }

        base.OnDetachedFromVisualTree(e);
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
