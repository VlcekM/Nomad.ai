using Avalonia.Controls;
using Nomad.Mobile.ViewModels;

namespace Nomad.Mobile.Views;

public partial class ChatsView : UserControl
{
    public ChatsView() => InitializeComponent();

    private async void OnRefreshRequested(object? sender, RefreshRequestedEventArgs e)
    {
        var deferral = e.GetDeferral();
        try
        {
            if (DataContext is ChatsViewModel chats)
            {
                await chats.RefreshAsync();
            }
        }
        finally
        {
            deferral.Complete();
        }
    }
}
