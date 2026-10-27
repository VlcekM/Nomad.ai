using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Nomad.App.ViewModels;
using Nomad.Core.Agents;

namespace Nomad.App.Views;

public partial class SessionView : UserControl
{
    private SessionViewModel? _vm;
    private bool _follow = true;

    public SessionView()
    {
        InitializeComponent();
        Composer.AddHandler(KeyDownEvent, OnComposerKeyDown, RoutingStrategies.Tunnel);
        AddHandler(KeyDownEvent, OnViewKeyDown, RoutingStrategies.Bubble);
        Scroller.ScrollChanged += OnScrollChanged;

        // Chips keep their hover fill while their menu is open, as in the original.
        TrackOpen(ModelChip, ModelChip.Flyout);
        TrackOpen(ModeChip, ModeChip.Flyout);
        TrackOpen(FolderChip, FlyoutBase.GetAttachedFlyout(FolderChip));
        TrackOpen(UsageChip, UsageChip.Flyout);

        // A click anywhere on the page puts the caret in the composer, unless it went to a control
        // of its own or selected some text.
        AddHandler(PointerReleasedEvent, OnPageClicked, RoutingStrategies.Bubble, handledEventsToo: true);

        // Below this width the full row (folder, mode, four model chips, effort, ring) would not fit.
        SizeChanged += (_, e) => StatusRow.Classes.Set("narrow", e.NewSize.Width < 940);
        if (UsageChip.Flyout is { } usage)
        {
            usage.Opened += (_, _) => _ = Vm?.Usage.RefreshAsync();
        }

        // The effort thumb glides while dragged and settles on the nearest stop when let go.
        EffortSlider.Released += (_, _) => Vm?.SnapEffort();

        // New transcript items, attachments and queued prompts fade and slide in; activity lines carry a class
        // so "Show activity" can leave them out.
        Transcript.ContainerPrepared += (_, e) =>
        {
            e.Container.Classes.Set("activity", e.Container.DataContext is ThinkingItem or ToolGroupItem);
            if (_animateNew && Vm is { IsLoadingHistory: false })
            {
                Controls.Entrance.Play(e.Container);
            }
        };
        AttachmentList.ContainerPrepared += (_, e) => Controls.Entrance.Play(e.Container);
        QueueList.ContainerPrepared += (_, e) => Controls.Entrance.Play(e.Container);

        // The "/" menu is as wide as the composer.
        ComposerBar.SizeChanged += (_, e) => CommandMenu.Width = e.NewSize.Width;

        // Images dropped anywhere on the session are attached; other files go in as their paths.
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DragLeaveEvent, (_, _) => ComposerBar.Classes.Remove("dragover"));
        AddHandler(DragDrop.DropEvent, OnDrop);
    }

    // Off while a session's items are first created (history, switching sessions): only new ones animate.
    private bool _animateNew;

    private static void TrackOpen(Button chip, FlyoutBase? flyout)
    {
        if (flyout is null)
        {
            return;
        }

        flyout.Opened += (_, _) => chip.Classes.Add("open");
        flyout.Closed += (_, _) => chip.Classes.Remove("open");
    }

    private SessionViewModel? Vm => DataContext as SessionViewModel;

    // ------------------------------------------------------------------ binding to the session

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        Attach(VisualRoot is not null ? Vm : null);
        _follow = true;
        ScrollDownButton.IsVisible = false;
        ScrollToEndSoon();
        FocusComposerSoon();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Attach(Vm);
        FocusComposerSoon();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        Attach(null);
    }

    private void Attach(SessionViewModel? vm)
    {
        if (ReferenceEquals(vm, _vm))
        {
            return;
        }

        if (_vm is not null)
        {
            _vm.ContentChanged -= OnContentChanged;
        }

        _vm = vm;
        _animateNew = false;
        if (_vm is not null)
        {
            _vm.ContentChanged += OnContentChanged;
            Dispatcher.UIThread.Post(() => _animateNew = true, DispatcherPriority.Background);
        }
    }

    // ------------------------------------------------------------------ auto-follow

    private void OnContentChanged()
    {
        if (_follow)
        {
            ScrollToEndSoon();
        }
    }

    private void OnScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        var atBottom = Scroller.Offset.Y >= Scroller.Extent.Height - Scroller.Viewport.Height - 24;
        if (Math.Abs(e.OffsetDelta.Y) > 0.5)
        {
            // The user (or ScrollToEnd) moved: follow again only once back at the bottom.
            _follow = atBottom;
        }
        else if ((e.ExtentDelta.Y != 0 || e.ViewportDelta.Y != 0) && _follow && !atBottom)
        {
            ScrollToEndSoon();
        }

        ScrollDownButton.IsVisible = !_follow && !atBottom;
    }

    private void ScrollToEndSoon() =>
        Dispatcher.UIThread.Post(() => Scroller.ScrollToEnd(), DispatcherPriority.Loaded);

    private void OnScrollDownClick(object? sender, RoutedEventArgs e)
    {
        _follow = true;
        Scroller.ScrollToEnd();
        ScrollDownButton.IsVisible = false;
    }

    private void FocusComposerSoon() =>
        Dispatcher.UIThread.Post(() => Composer.Focus(), DispatcherPriority.Loaded);

    // ------------------------------------------------------------------ keyboard

    private void OnComposerKeyDown(object? sender, KeyEventArgs e)
    {
        if (Vm is not { } vm)
        {
            return;
        }

        // The "/" menu takes the arrows, Tab, Enter and Esc while it is open.
        if (vm.IsCommandMenuOpen && vm.SelectedCommand is { } command)
        {
            switch (e.Key)
            {
                case Key.Up or Key.Down:
                    vm.MoveCommandSelection(e.Key == Key.Up ? -1 : 1);
                    ScrollSelectedCommandIntoView();
                    e.Handled = true;
                    return;
                case Key.Tab:
                case Key.Enter when (e.KeyModifiers & KeyModifiers.Shift) == 0:
                    var complete = vm.AcceptCommand(command);
                    Composer.CaretIndex = Composer.Text?.Length ?? 0;
                    e.Handled = true;
                    if (e.Key == Key.Enter && complete)
                    {
                        _follow = true;
                        vm.SendCommand.Execute(null);
                    }

                    return;
                case Key.Escape:
                    vm.DismissCommandMenu();
                    e.Handled = true;
                    return;
            }
        }

        // Ctrl+V with an image (or image files) on the clipboard attaches it; text pastes as usual.
        if ((e.Key == Key.V && e.KeyModifiers == KeyModifiers.Control) || (e.Key == Key.Insert && e.KeyModifiers == KeyModifiers.Shift))
        {
            if (Infrastructure.PromptImages.FromClipboard() is { Count: > 0 } images)
            {
                vm.AddAttachments(images);
                e.Handled = true;
            }

            return;
        }

        switch (e.Key)
        {
            case Key.Enter when (e.KeyModifiers & KeyModifiers.Shift) == 0:
                e.Handled = true;
                if (vm.PendingPermission is not null && string.IsNullOrWhiteSpace(vm.ComposerText))
                {
                    vm.AnswerPending(PermissionChoice.Allow);
                }
                else if (vm.SendCommand.CanExecute(null))
                {
                    _follow = true;
                    vm.SendCommand.Execute(null);
                }

                break;

            case Key.Escape:
                e.Handled = HandleEscape(vm);
                break;

            case >= Key.D1 and <= Key.D4 when e.KeyModifiers == KeyModifiers.Control:
                var index = e.Key - Key.D1;
                if (index < vm.ModelOptions.Count)
                {
                    vm.SelectModelCommand.Execute(vm.ModelOptions[index].Id);
                }

                e.Handled = true;
                break;
        }
    }

    /// <summary>Enter / Esc anywhere in the session (outside text boxes) answer a pending permission.</summary>
    private void OnViewKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled || Vm is not { } vm || e.Source is TextBox)
        {
            return;
        }

        if (e.Key == Key.Enter && vm.PendingPermission is not null)
        {
            vm.AnswerPending(PermissionChoice.Allow);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = HandleEscape(vm);
        }
    }

    /// <summary>Enter in a question card's free-text box submits the answers, Esc skips the question.</summary>
    private void OnQuestionOtherKeyDown(object? sender, KeyEventArgs e)
    {
        if (Vm is not { } vm || e.Key is not (Key.Enter or Key.Escape))
        {
            return;
        }

        vm.AnswerPending(e.Key == Key.Enter ? PermissionChoice.Allow : PermissionChoice.Deny);
        e.Handled = true;
        if (vm.PendingPermission is null)
        {
            FocusComposerSoon();
        }
    }

    private static bool HandleEscape(SessionViewModel vm)
    {
        if (vm.PendingPermission is not null)
        {
            vm.AnswerPending(PermissionChoice.Deny);
            return true;
        }

        if (vm.IsTurnRunning)
        {
            vm.StopCommand.Execute(null);
            return true;
        }

        return false;
    }

    // ------------------------------------------------------------------ menus

    /// <summary>Opens a status-row menu by name (dev hook <c>--dev-menu</c>).</summary>
    public void OpenMenu(string name)
    {
        switch (name)
        {
            case "model":
                ModelChip.Flyout?.ShowAt(ModelChip);
                break;
            case "mode":
                ModeChip.Flyout?.ShowAt(ModeChip);
                break;
            case "folder":
                FlyoutBase.ShowAttachedFlyout(FolderChip);
                break;
            case "plus":
                PlusButton.Flyout?.ShowAt(PlusButton);
                break;
            case "usage":
                UsageChip.Flyout?.ShowAt(UsageChip);
                break;
            case "remote":
                RemoteChip.Flyout?.ShowAt(RemoteChip);
                break;
        }
    }

    private void HideMenus()
    {
        ModelChip.Flyout?.Hide();
        ModeChip.Flyout?.Hide();
        PlusButton.Flyout?.Hide();
        UsageChip.Flyout?.Hide();
        RemoteChip.Flyout?.Hide();
        FlyoutBase.GetAttachedFlyout(FolderChip)?.Hide();
    }

    private void OnFolderChipClick(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm)
        {
            return;
        }

        if (vm.IsNew)
        {
            vm.FolderFilter = "";
            vm.RefreshFolders();
            FlyoutBase.ShowAttachedFlyout(FolderChip);
        }
        else if (vm.Folder is { } folder && Directory.Exists(folder))
        {
            // A running conversation keeps its folder; the chip reveals it instead.
            try
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = false });
            }
            catch (Exception)
            {
                // Explorer not available: nothing to do.
            }
        }
    }

    private void OnFolderOptionClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: MenuOption option })
        {
            Vm?.SetFolder(option.Id);
        }

        HideMenus();
    }

    private void OnNoFolderClick(object? sender, RoutedEventArgs e)
    {
        Vm?.SetFolder(null);
        HideMenus();
    }

    private async void OnOpenFolderClick(object? sender, RoutedEventArgs e)
    {
        HideMenus();
        if (Vm is not { } vm || TopLevel.GetTopLevel(this) is not { } top)
        {
            return;
        }

        var result = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose a folder for Claude",
            AllowMultiple = false,
        });

        if (result.Count > 0 && result[0].TryGetLocalPath() is { } path)
        {
            vm.SetFolder(path);
        }

        FocusComposerSoon();
    }

    private void OnModeOptionClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: MenuOption option })
        {
            Vm?.SelectModeCommand.Execute(option.Id);
        }

        HideMenus();
    }

    private void OnModelOptionClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: MenuOption option })
        {
            Vm?.SelectModelCommand.Execute(option.Id);
        }

        HideMenus();
    }

    /// <summary>A status-row model chip: picks that model, or opens the full picker when it is already the one.</summary>
    private void OnModelChipClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: MenuOption option })
        {
            if (option.IsSelected)
            {
                ModelChip.Flyout?.ShowAt(ModelChip);
            }
            else
            {
                Vm?.SelectModelCommand.Execute(option.Id);
            }
        }
    }

    private void OnPageClicked(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton != MouseButton.Left || Composer.IsFocused || Controls.SelectionAssist.IsSelecting)
        {
            return;
        }

        for (var visual = e.Source as Visual; visual is not null && !ReferenceEquals(visual, this); visual = visual.GetVisualParent())
        {
            switch (visual)
            {
                case Button or TextBox or ToggleButton or ComboBox or Slider or Controls.FillSlider or ScrollBar:
                    return;
                case SelectableTextBlock { SelectedText.Length: > 0 }:
                    return;
            }
        }

        Composer.Focus();
    }

    /// <summary>A click on the composer bar around the text (not on its buttons) puts the caret in it.</summary>
    private void OnComposerBarPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.Handled)
        {
            Composer.Focus();
            Composer.CaretIndex = Composer.Text?.Length ?? 0;
        }
    }

    // ------------------------------------------------------------------ composer: commands, images, queue

    private void ScrollSelectedCommandIntoView() =>
        Dispatcher.UIThread.Post(() =>
        {
            var selected = CommandScroller.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.Classes.Contains("selected"));
            selected?.BringIntoView();
        }, DispatcherPriority.Loaded);

    private void OnCommandOptionClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: CommandOption option } && Vm is { } vm)
        {
            vm.AcceptCommand(option);
            Composer.Focus();
            Composer.CaretIndex = Composer.Text?.Length ?? 0;
        }
    }

    private void OnRemoveAttachmentClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: Infrastructure.Attachment image })
        {
            Vm?.RemoveAttachmentCommand.Execute(image);
        }
    }

    private void OnRemoveQueuedClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: QueuedPrompt prompt })
        {
            Vm?.RemoveQueuedCommand.Execute(prompt);
        }
    }

    private async void OnAttachFilesClick(object? sender, RoutedEventArgs e)
    {
        HideMenus();
        if (Vm is not { } vm || TopLevel.GetTopLevel(this) is not { } top)
        {
            return;
        }

        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Attach files",
            AllowMultiple = true,
            FileTypeFilter =
            [
                new FilePickerFileType("All files") { Patterns = ["*"] },
                new FilePickerFileType("Images") { Patterns = ["*.png", "*.jpg", "*.jpeg", "*.gif", "*.webp", "*.bmp"] },
            ],
        });

        vm.AddAttachments(files.Select(f => f.TryGetLocalPath()).OfType<string>().Select(Infrastructure.PromptImages.FromPath));
        FocusComposerSoon();
    }

    private static IEnumerable<string> DroppedFiles(DragEventArgs e) =>
        e.Data.GetFiles()?.Select(f => f.TryGetLocalPath()).OfType<string>() ?? [];

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        var any = Vm is { IsPhoneSession: false } && DroppedFiles(e).Any();
        e.DragEffects = any ? DragDropEffects.Copy : DragDropEffects.None;
        ComposerBar.Classes.Set("dragover", any);
    }

    // Images are attached as images, anything else (files, folders) as chips that send their paths.
    private void OnDrop(object? sender, DragEventArgs e)
    {
        ComposerBar.Classes.Remove("dragover");
        if (Vm is not { IsPhoneSession: false } vm)
        {
            return;
        }

        vm.AddAttachments(DroppedFiles(e).Select(Infrastructure.PromptImages.FromPath).ToList());
        Composer.Focus();
        Composer.CaretIndex = Composer.Text?.Length ?? 0;
        e.Handled = true;
    }

    // ------------------------------------------------------------------ prompt actions

    private async void OnCopyMessageClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: UserMessageItem message } && TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(message.Text);
        }
    }

    private void OnRewindClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: UserMessageItem message } && Vm is { } vm)
        {
            _ = vm.RewindAsync(message);
            Composer.Focus();
        }
    }

    private void OnForkClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: UserMessageItem message } && Vm is { } vm
            && TopLevel.GetTopLevel(this)?.DataContext is MainViewModel main)
        {
            main.Fork(vm, message);
        }
    }

    private async void OnCopyRemoteLinkClick(object? sender, RoutedEventArgs e)
    {
        HideMenus();
        if (Vm?.RemoteControlUrl is { } url && TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(url);
        }
    }

    private void OnOpenRemoteLinkClick(object? sender, RoutedEventArgs e)
    {
        HideMenus();
        if (Vm?.RemoteControlUrl is not { } url)
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception)
        {
            // No browser registered: nothing to open.
        }
    }

    private void OnUsagePageClick(object? sender, RoutedEventArgs e)
    {
        HideMenus();
        try
        {
            Process.Start(new ProcessStartInfo(UsageViewModel.UsagePageUrl) { UseShellExecute = true });
        }
        catch (Exception)
        {
            // No browser registered: nothing to open.
        }
    }

    private void OnSettingsClick(object? sender, RoutedEventArgs e)
    {
        HideMenus();
        if (TopLevel.GetTopLevel(this)?.DataContext is MainViewModel main)
        {
            main.OpenSettingsCommand.Execute(null);
        }
    }
}
