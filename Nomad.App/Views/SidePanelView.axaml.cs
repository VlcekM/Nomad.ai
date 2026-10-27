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

        // Folded cards stay folded (taken at once: nothing is laid out yet).
        _restored = true;
        foreach (var header in this.GetLogicalDescendants().OfType<Button>().Where(b => b.Classes.Contains("cardheader")))
        {
            if (CardOf(header) is { } card)
            {
                var open = !main.Settings.CollapsedGroups.Contains(Key(header));
                Fold.SetIsOpen(card.Content, open);
                SetChevron(header, open);
            }
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
        Start(new ProcessStartInfo(UsageViewModel.UsagePageUrl) { UseShellExecute = true });

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
