using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Nomad.Mobile.Markdown;
using Nomad.Mobile.ViewModels;

namespace Nomad.Mobile.Views;

public partial class ChatView : UserControl
{
    private ChatViewModel? _chat;

    // A chat just opened follows its first page to the end; after that only while the reader is near the end.
    private bool _firstPage = true;
    private bool _scrollPending;

    public ChatView() => InitializeComponent();

    protected override void OnDataContextChanged(EventArgs e)
    {
        if (_chat is not null)
        {
            _chat.EntriesAppended -= OnEntriesAppended;
        }

        _chat = DataContext as ChatViewModel;
        _firstPage = true;
        _scrollPending = false;
        if (_chat is not null)
        {
            _chat.EntriesAppended += OnEntriesAppended;
        }

        base.OnDataContextChanged(e);
    }

    /// <summary>New entries at the end: follow them, unless the user has scrolled up to read.</summary>
    private void OnEntriesAppended()
    {
        if (_scrollPending)
        {
            return;
        }

        var scroller = Scroller;
        var nearEnd = scroller.Offset.Y >= scroller.Extent.Height - scroller.Viewport.Height - 120;
        if (nearEnd || _firstPage)
        {
            _firstPage = false;
            _scrollPending = true;
            Dispatcher.UIThread.Post(() =>
            {
                _scrollPending = false;
                scroller.ScrollToEnd();
            }, DispatcherPriority.Background);
        }
    }

    /// <summary>The + beside the message box: the phone's own file picker, any number of files; they attach to the message.</summary>
    private async void OnAttachClick(object? sender, RoutedEventArgs e)
    {
        if (_chat is not { } chat || TopLevel.GetTopLevel(this)?.StorageProvider is not { CanOpen: true } storage)
        {
            return;
        }

        try
        {
            var picked = await storage.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Attach files", AllowMultiple = true });
            var files = new List<PickedFile>();
            var seen = new HashSet<Uri>();
            foreach (var file in picked)
            {
                // Some Android pickers put a single selection in both Intent.Data and Intent.ClipData, so Avalonia hands it back twice.
                if (file.Path is { } path && !seen.Add(path))
                {
                    continue;
                }

                long? size = null;
                try
                {
                    size = (long?)(await file.GetBasicPropertiesAsync()).Size;
                }
                catch (Exception)
                {
                    // Not every source says; the hub checks it anyway.
                }

                files.Add(new PickedFile(file.Name, size, file.OpenReadAsync));
            }

            chat.AddFiles(files);
        }
        catch (Exception ex)
        {
            chat.ShowProblem("Couldn't open the files: " + ex.Message);
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

    /// <summary>Hold a message: below Copy text, an Open item for each web or mail link in it, the same as tapping the link.</summary>
    private void OnEntryMenuOpening(object? sender, EventArgs e)
    {
        if (sender is not MenuFlyout { Target.DataContext: EntryViewModel entry } menu)
        {
            return;
        }

        foreach (var old in menu.Items.OfType<MenuItem>().Where(i => i.Tag is string).ToList())
        {
            menu.Items.Remove(old);
        }

        foreach (var url in MarkdownBlock.LinksIn(entry.IsCard ? entry.CardText : entry.Text).Take(5))
        {
            var item = new MenuItem { Header = "Open " + Shortened(url), Tag = url };
            item.Click += async (_, _) => await OpenLinkAsync(url);
            menu.Items.Add(item);
        }
    }

    private static string Shortened(string url)
    {
        var shown = url.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase) ? url[7..] : url.Replace("https://", "").Replace("http://", "");
        return shown.Length > 40 ? shown[..39] + "…" : shown;
    }

    private async Task OpenLinkAsync(string url)
    {
        if (!await MarkdownBlock.OpenAsync(this, url))
        {
            _chat?.ShowProblem("Couldn't open " + url);
        }
    }
}
