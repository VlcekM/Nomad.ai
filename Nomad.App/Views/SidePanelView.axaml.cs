using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Nomad.App.Controls;
using Nomad.App.ViewModels;

namespace Nomad.App.Views;

public partial class SidePanelView : UserControl
{
    private bool _restored;

    public SidePanelView()
    {
        InitializeComponent();
        ApplyFontSizes();
        if (Application.Current is { } app)
        {
            app.ResourcesChanged += (_, _) => ApplyFontSizes();
        }
    }

    /// <summary>The panel reads a point larger than the chrome: its own copies of the interface sizes, one up.</summary>
    private void ApplyFontSizes()
    {
        if (Application.Current?.Resources.TryGetValue("UiFontSize", out var value) != true || value is not double size
            || (Resources.TryGetValue("UiFontSize", out var mine) && mine is double current && current == size + 1))
        {
            return;
        }

        Resources["UiFontSizeTiny"] = size - 1;
        Resources["UiFontSizeSmall"] = size;
        Resources["UiFontSize"] = size + 1;
        Resources["UiFontSizeLarge"] = size + 2;
        FontSize = size + 1;
    }

    private SessionViewModel? Vm => DataContext as SessionViewModel;

    private MainViewModel? Main => TopLevel.GetTopLevel(this)?.DataContext as MainViewModel;

    private static string Key(Button header) => "Panel:" + header.Tag;

    /// <summary>A card: its header button, and the content under it that folds.</summary>
    private static (Button Header, Control Content)? CardOf(Button header)
    {
        var card = header.FindLogicalAncestorOfType<Border>();
        return card?.Child is StackPanel { Children: [_, Control content] } ? (header, content) : null;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (_restored || Main is not { } main)
        {
            return;
        }

        // Folded cards stay folded, and turned-off cards stay off (taken at once: nothing is laid out yet). The content's
        // visibility is set too: Fold.IsOpen starts false, so folding a card that way alone would change nothing and
        // leave it showing under a folded chevron.
        _restored = true;
        foreach (var header in Headers())
        {
            if (CardOf(header) is { } card)
            {
                var open = !main.Settings.CollapsedGroups.Contains(Key(header));
                Fold.SetIsOpen(card.Content, open);
                card.Content.IsVisible = open;
                SetChevron(header, open);
            }

            if (CardBorder(header) is { } border)
            {
                var shown = !main.Settings.HiddenPanelCards.Contains((string)header.Tag!);
                Fold.SetIsOpen(border, shown);
                border.IsVisible = shown;
            }
        }
    }

    private IEnumerable<Button> Headers() =>
        this.GetLogicalDescendants().OfType<Button>().Where(b => b.Classes.Contains("cardheader"));

    private static Border? CardBorder(Button header) => header.FindLogicalAncestorOfType<Border>();

    /// <summary>The cards menu shows which cards are on.</summary>
    private void OnCardsMenuOpening(object? sender, EventArgs e)
    {
        if (Main is not { } main)
        {
            return;
        }

        foreach (var item in CardsMenu.Children.OfType<Button>())
        {
            if (item.Tag is string key && item.GetLogicalDescendants().OfType<Border>().FirstOrDefault(b => b.Classes.Contains("switch")) is { } toggle)
            {
                toggle.Classes.Set("on", !main.Settings.HiddenPanelCards.Contains(key));
            }
        }
    }

    /// <summary>Turns a card on or off: it folds away (or unfolds) and the choice is remembered.</summary>
    private void OnCardToggleClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string key } item || Main is not { } main)
        {
            return;
        }

        var shown = main.Settings.HiddenPanelCards.Remove(key);
        if (!shown)
        {
            main.Settings.HiddenPanelCards.Add(key);
        }

        main.SaveSettings();
        if (item.GetLogicalDescendants().OfType<Border>().FirstOrDefault(b => b.Classes.Contains("switch")) is { } toggle)
        {
            toggle.Classes.Set("on", shown);
        }

        if (Headers().FirstOrDefault(h => (string?)h.Tag == key) is { } header && CardBorder(header) is { } border)
        {
            Fold.SetIsOpen(border, shown);
        }
    }

    private void OnHeaderClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button header || CardOf(header) is not { } card || Main is not { } main)
        {
            return;
        }

        var open = !Fold.GetIsOpen(card.Content);
        Fold.SetIsOpen(card.Content, open);
        SetChevron(header, open);
        main.Settings.CollapsedGroups.Remove(Key(header));
        if (!open)
        {
            main.Settings.CollapsedGroups.Add(Key(header));
        }

        main.SaveSettings();
    }

    private static void SetChevron(Button header, bool open)
    {
        foreach (var icon in header.GetLogicalDescendants().OfType<Icon>().Where(i => i.Classes.Contains("fold")))
        {
            icon.Classes.Set("closed", !open);
        }
    }

    private void OnSubagentClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: SubagentViewModel agent } && Vm is { } vm)
        {
            vm.OpenSubagent = agent;
        }
    }

    private void OnWorkflowClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: WorkflowRunItem run } && Vm is { } vm)
        {
            vm.OpenWorkflowCommand.Execute(run);
        }
    }

    private void OnExplorerClick(object? sender, RoutedEventArgs e)
    {
        if (Vm?.Folder is { } folder && Directory.Exists(folder))
        {
            Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { folder } });
        }
    }

    /// <summary>Windows Terminal in the folder, or a console where it is not installed.</summary>
    private void OnTerminalClick(object? sender, RoutedEventArgs e)
    {
        if (Vm?.Folder is not { } folder || !Directory.Exists(folder))
        {
            return;
        }

        if (!Start(new ProcessStartInfo("wt.exe") { ArgumentList = { "-d", folder }, UseShellExecute = true }))
        {
            Start(new ProcessStartInfo("cmd.exe") { WorkingDirectory = folder, UseShellExecute = true });
        }
    }

    private async void OnCopyPathClick(object? sender, RoutedEventArgs e)
    {
        if (Vm?.Folder is { } folder && TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(folder);
        }
    }

    private void OnUsagePageClick(object? sender, RoutedEventArgs e) =>
        Start(new ProcessStartInfo(Vm?.Usage.PageUrl ?? UsageViewModel.UsagePageUrl) { UseShellExecute = true });

    private static bool Start(ProcessStartInfo start)
    {
        try
        {
            Process.Start(start)?.Dispose();
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }
}
