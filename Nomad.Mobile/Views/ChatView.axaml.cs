using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Nomad.Mobile.ViewModels;

namespace Nomad.Mobile.Views;

public partial class ChatView : UserControl
{
    private ChatViewModel? _chat;

    // A chat just opened follows its first page to the end; after that only while the reader is near the end.
    private bool _firstPage = true;

    public ChatView() => InitializeComponent();

    protected override void OnDataContextChanged(EventArgs e)
    {
        if (_chat is not null)
        {
            _chat.EntriesAppended -= OnEntriesAppended;
        }

        _chat = DataContext as ChatViewModel;
        _firstPage = true;
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
        if (nearEnd || _firstPage)
        {
            _firstPage = false;
            Dispatcher.UIThread.Post(scroller.ScrollToEnd, DispatcherPriority.Background);
        }
    }

    /// <summary>Hold a message, Copy text: its whole text, since the text itself isn't selectable (a drag on it scrolls).</summary>
    private async void OnCopyEntryClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: EntryViewModel entry } && TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(entry.IsCard ? entry.CardText : entry.Text);
        }
    }
}
