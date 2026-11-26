using Avalonia.Controls;
using Avalonia.Threading;
using Nomad.Mobile.ViewModels;

namespace Nomad.Mobile.Views;

public partial class ChatView : UserControl
{
    private ChatViewModel? _chat;

    public ChatView() => InitializeComponent();

    protected override void OnDataContextChanged(EventArgs e)
    {
        if (_chat is not null)
        {
            _chat.EntriesAppended -= OnEntriesAppended;
        }

        _chat = DataContext as ChatViewModel;
        if (_chat is not null)
        {
            _chat.EntriesAppended += OnEntriesAppended;
        }

        base.OnDataContextChanged(e);
    }

    /// <summary>New entries at the end: follow them, unless the user has scrolled up to read.</summary>
    private void OnEntriesAppended()
    {
        var scroller = Scroller;
        var nearEnd = scroller.Offset.Y >= scroller.Extent.Height - scroller.Viewport.Height - 120;
        if (nearEnd || scroller.Offset.Y == 0)
        {
            Dispatcher.UIThread.Post(scroller.ScrollToEnd, DispatcherPriority.Background);
        }
    }
}
