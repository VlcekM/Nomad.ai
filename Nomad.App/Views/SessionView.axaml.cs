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
        Composer.AddHandler(ContextRequestedEvent, OnComposerContextRequested, RoutingStrategies.Tunnel);
        Composer.ContextMenu = BuildComposerMenu();
        Composer.AddHandler(KeyDownEvent, OnComposerCtrlDown, RoutingStrategies.Tunnel, handledEventsToo: true);
        Composer.AddHandler(KeyUpEvent, (_, e) =>
        {
            if (e.Key is Key.LeftCtrl or Key.RightCtrl)
            {
                ShowSendInstantly(false);
            }
        }, RoutingStrategies.Tunnel, handledEventsToo: true);
        Composer.LostFocus += (_, _) => ShowSendInstantly(false);
        SendButton.AddHandler(PointerPressedEvent, (_, e) =>
        {
            if (e.KeyModifiers == KeyModifiers.Control && e.GetCurrentPoint(SendButton).Properties.IsLeftButtonPressed
                && Vm is { } vm && vm.SendNowFromComposer())
            {
                _follow = true;
                ShowSendInstantly(false);
                e.Handled = true;
            }
        }, RoutingStrategies.Tunnel);
        AddHandler(KeyDownEvent, OnViewKeyDown, RoutingStrategies.Bubble);
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
        QueueList.AddHandler(KeyDownEvent, OnQueuedEditKeyDown, RoutingStrategies.Tunnel);
        Scroller.ScrollChanged += OnScrollChanged;

        // Chips keep their hover fill while their menu is open, as in the original.
        TrackOpen(ModelChip, ModelChip.Flyout);
        TrackOpen(ModeRow, FlyoutBase.GetAttachedFlyout(ModeRow));
        TrackOpen(FolderChip, FlyoutBase.GetAttachedFlyout(FolderChip));
        TrackOpen(UsageChip, UsageChip.Flyout);
        if (ModelChip.Flyout is { } models)
        {
            models.Closed += (_, _) => FlyoutBase.GetAttachedFlyout(ModeRow)?.Hide();
        }

        // A click anywhere on the page puts the caret in the composer, unless it went to a control
        // of its own or selected some text.
        AddHandler(PointerReleasedEvent, OnPageClicked, RoutingStrategies.Bubble, handledEventsToo: true);

        // Below these widths the full row (folder, git, four model chips, effort, ring) would not fit: first the
        // other model chips go, then the branch name and the effort's name.
        SizeChanged += (_, e) =>
        {
            StatusRow.Classes.Set("narrow", e.NewSize.Width < 1100);
            StatusRow.Classes.Set("tight", e.NewSize.Width < 880);
        };
        if (UsageChip.Flyout is { } usage)
        {
            usage.Opened += (_, _) => _ = Vm?.Usage.RefreshAsync();
        }

        // The effort thumb glides while dragged and settles on the nearest stop when let go.
        EffortSlider.Released += (_, _) => Vm?.SnapEffort();
        MenuEffortSlider.Released += (_, _) => Vm?.SnapEffort();

        // New transcript items, attachments and queued prompts fade and slide in; activity lines carry a class
        // so "Show activity" can leave them out. The transcript is virtualized, so its containers are prepared again
        // whenever they scroll into view: only items added since the session was shown animate, once.
        Transcript.ItemTemplate = new Controls.FreshTemplate(DataTemplates);
        Transcript.ContainerPrepared += (_, e) =>
        {
            e.Container.Classes.Set("activity", e.Container.DataContext is ThinkingItem or ToolGroupItem);
            if (e.Container.DataContext is { } item && _fresh.Remove(item))
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
        // Moving from one of its controls to another leaves the one and enters the next in the same breath, so the
        // hint stays put; leaving the window takes it away.
        AddHandler(DragDrop.DragEnterEvent, OnDragOver);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DragLeaveEvent, (_, _) => ShowDropHint(false));
        AddHandler(DragDrop.DropEvent, OnDrop);
    }

    // Off while a session's items are first created (history, switching sessions): only new ones animate.
    private bool _animateNew;

    // Transcript items added since, not yet shown: their entrance plays when their container is first prepared.
    private readonly HashSet<object> _fresh = [];

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
            _vm.Items.CollectionChanged -= OnItemsChanged;
        }

        _vm = vm;
        _animateNew = false;
        _fresh.Clear();
        if (_vm is not null)
        {
            _vm.ContentChanged += OnContentChanged;
            _vm.Items.CollectionChanged += OnItemsChanged;
            Dispatcher.UIThread.Post(() => _animateNew = true, DispatcherPriority.Background);
        }
    }

    private void OnItemsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset)
        {
            _fresh.Clear();
        }
        else if (e.OldItems is { } old)
        {
            foreach (var item in old)
            {
                _fresh.Remove(item);
            }
        }

        if (e.NewItems is { } added && _animateNew && Vm is { IsLoadingHistory: false })
        {
            foreach (var item in added)
            {
                _fresh.Add(item);
            }
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
        var atBottom = AtBottom();
        TopFade.Opacity = Scroller.Offset.Y > 4 ? 1 : 0;
        if (_seekingEnd)
        {
            // On the way to the end: the items realized there are seldom as tall as the virtualized list guessed, so
            // the end moves on as it is reached; keep after it until really there.
            _seekingEnd = !atBottom;
            if (_seekingEnd)
            {
                ScrollToEndSoon();
            }
        }
        else if (Math.Abs(e.OffsetDelta.Y) > 0.5)
        {
            // The user moved: follow again only once back at the bottom.
            _follow = atBottom;
        }
        else if ((e.ExtentDelta.Y != 0 || e.ViewportDelta.Y != 0) && _follow && !atBottom)
        {
            ScrollToEndSoon();
        }

        ScrollDownButton.IsVisible = !_follow && !atBottom;
    }

    // Set while ScrollToEnd chases an end that moves as the last items are realized (see OnScrollChanged).
    private bool _seekingEnd;

    private void ScrollToEndSoon() =>
        Dispatcher.UIThread.Post(ScrollToEnd, DispatcherPriority.Loaded);

    private void ScrollToEnd()
    {
        // Already there, nothing moves and no ScrollChanged would end the chase.
        _seekingEnd = !AtBottom();
        Scroller.ScrollToEnd();
    }

    private bool AtBottom() => Scroller.Offset.Y >= Scroller.Extent.Height - Scroller.Viewport.Height - 24;

    private void OnScrollDownClick(object? sender, RoutedEventArgs e)
    {
        _follow = true;
        ScrollToEnd();
        ScrollDownButton.IsVisible = false;
    }

    /// <summary>The status row's git chips (branch, worktree switch), which glide to and from the title bar's.</summary>
    public (Control Branch, Control Worktree) GitChips => (BranchChip, WorktreeToggle);

    public void FocusComposerSoon() =>
        Dispatcher.UIThread.Post(() => Composer.Focus(), DispatcherPriority.Loaded);

    // ------------------------------------------------------------------ keyboard

    private bool TakeSuggestion()
    {
        if (Vm?.AcceptSuggestion() != true)
        {
            return false;
        }

        Composer.Focus();
        Composer.CaretIndex = Composer.Text?.Length ?? 0;
        return true;
    }

    // A right-click on the empty composer takes the suggestion instead of opening the edit menu.
    private void OnComposerContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (Vm is { HasSuggestion: true } && TakeSuggestion())
        {
            e.Handled = true;
            return;
        }

        // Tunnels in before the menu opens, so its items show what applies right now.
        if (Composer.ContextMenu is { } menu)
        {
            var items = menu.Items.OfType<MenuItem>().ToList();
            items[0].IsEnabled = Composer.CanCut;
            items[1].IsEnabled = Composer.CanCopy;
            items[2].IsEnabled = Composer.CanPaste;
            items[3].IsEnabled = !string.IsNullOrEmpty(Composer.Text);
        }
    }

    // The composer's theme has no edit menu of its own. Paste attaches images like Ctrl+V does.
    private ContextMenu BuildComposerMenu()
    {
        MenuItem Item(string header, string gesture, Action action)
        {
            var item = new MenuItem { Header = header, InputGesture = KeyGesture.Parse(gesture) };
            item.Click += (_, _) =>
            {
                Composer.Focus();
                action();
            };
            return item;
        }

        var menu = new ContextMenu();
        menu.Items.Add(Item("Cut", "Ctrl+X", Composer.Cut));
        menu.Items.Add(Item("Copy", "Ctrl+C", Composer.Copy));
        menu.Items.Add(Item("Paste", "Ctrl+V", () =>
        {
            if (Infrastructure.PromptImages.FromClipboard() is { Count: > 0 } images)
            {
                Vm?.AddAttachments(images);
            }
            else
            {
                Composer.Paste();
            }
        }));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Select all", "Ctrl+A", Composer.SelectAll));
        return menu;
    }

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

        // A suggested prompt (ghost text in the empty composer): Tab or → takes it, Esc dismisses it.
        if (vm.HasSuggestion && e.KeyModifiers == KeyModifiers.None)
        {
            switch (e.Key)
            {
                case Key.Tab or Key.Right when TakeSuggestion():
                    e.Handled = true;
                    return;
                case Key.Escape:
                    vm.Suggestion = null;
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
            // While Claude works, Ctrl+Enter stops it and sends at once.
            case Key.Enter when e.KeyModifiers == KeyModifiers.Control && vm.SendNowFromComposer():
                _follow = true;
                e.Handled = true;
                break;

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

            // ↑ in an empty composer edits the last queued prompt.
            case Key.Up when e.KeyModifiers == KeyModifiers.None && vm.ComposerText.Length == 0 && !vm.HasAttachments && vm.Queued.Count > 0:
                BeginQueuedEdit(vm.Queued[^1]);
                e.Handled = true;
                break;

            // Ctrl+Shift+1…4: the model (Ctrl+1…9 alone opens a session from the sidebar).
            case >= Key.D1 and <= Key.D4 when e.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Shift):
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

        // A running turn is stopped with the Stop button only: Esc no longer interrupts it.
        return false;
    }

    // ------------------------------------------------------------------ menus

    /// <summary>Opens a status-row menu by name (dev hook <c>--dev-menu</c>).</summary>
    public void OpenMenu(string name)
    {
        switch (name)
        {
            case "composer":
                // Asked for as a right-click asks, so the items' enabled states are set.
                Composer.RaiseEvent(new ContextRequestedEventArgs { RoutedEvent = ContextRequestedEvent, Source = Composer });
                if (Composer.ContextMenu is { IsOpen: false } composerMenu)
                {
                    composerMenu.Open(Composer);
                }

                break;
            case "model":
                ModelChip.Flyout?.ShowAt(ModelChip);
                break;
            case "mode":
                ModelChip.Flyout?.ShowAt(ModelChip);
                Dispatcher.UIThread.Post(() => FlyoutBase.ShowAttachedFlyout(ModeRow), DispatcherPriority.Loaded);
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
            case "branch":
                OnBranchChipClick(BranchChip, new RoutedEventArgs());
                break;
        }
    }

    private void HideMenus()
    {
        ModelChip.Flyout?.Hide();
        FlyoutBase.GetAttachedFlyout(ModeRow)?.Hide();
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

    /// <summary>A project card: work in its folder, and carry on typing.</summary>
    private void OnProjectClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: ProjectViewModel project })
        {
            Vm?.SetFolder(project.Path);
            FocusComposerSoon();
        }
    }

    private void OnWorktreeClick(object? sender, RoutedEventArgs e)
    {
        if (Vm is { } vm)
        {
            vm.UseWorktree = !vm.UseWorktree;
        }
    }

    /// <summary>The git chip opens the branch menu on a new session; a running one keeps its branch (the tooltip says which).</summary>
    private async void OnBranchChipClick(object? sender, RoutedEventArgs e)
    {
        if (Vm is { IsNew: true } vm)
        {
            await vm.LoadBranchesAsync();
            FlyoutBase.ShowAttachedFlyout(BranchChip);
        }
    }

    private async void OnBranchOptionClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: MenuOption option } && Vm is { } vm && await vm.PickBranchAsync(option.Id))
        {
            FlyoutBase.GetAttachedFlyout(BranchChip)?.Hide();
            FocusComposerSoon();
        }
    }

    /// <summary>Pins or unpins a card; the cards glide to their new places (220 ms, cubic ease-out), new ones fade in.</summary>
    private void OnPinClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is Control { DataContext: ProjectViewModel project } && Vm is { } vm)
        {
            RearrangeCards(() => vm.TogglePin(project));
        }
    }

    /// <summary>
    /// Takes a card off the page: it fades out (150 ms), then the rest glide into the gap and a card from further back
    /// fades in.
    /// </summary>
    private void OnRemoveProjectClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is not Control { DataContext: ProjectViewModel project } || Vm is not { } vm)
        {
            return;
        }

        var card = ProjectCards.GetRealizedContainers().FirstOrDefault(c => c.DataContext == project);
        if (card is null || TopLevel.GetTopLevel(card) is not { } top)
        {
            RearrangeCards(() => vm.RemoveProject(project));
            return;
        }

        card.IsHitTestVisible = false;
        TimeSpan? start = null;
        void Step(TimeSpan time)
        {
            start ??= time;
            var t = Math.Clamp((time - start.Value).TotalMilliseconds / 150, 0, 1);
            card.Opacity = Math.Pow(1 - t, 3);
            if (t < 1)
            {
                top.RequestAnimationFrame(Step);
                return;
            }

            card.Opacity = 1;
            card.IsHitTestVisible = true;
            RearrangeCards(() => vm.RemoveProject(project));
        }

        top.RequestAnimationFrame(Step);
    }

    /// <summary>Changes the cards; those that stay glide to their new places (220 ms, cubic ease-out), new ones fade in.</summary>
    private void RearrangeCards(Action change)
    {
        var before = new Dictionary<object, Point>();
        foreach (var c in ProjectCards.GetRealizedContainers())
        {
            if (c.DataContext is { } item && c.TranslatePoint(default, ProjectCards) is { } p)
            {
                before[item] = p;
            }
        }

        change();
        ProjectCards.UpdateLayout();
        foreach (var c in ProjectCards.GetRealizedContainers())
        {
            if (c.DataContext is { } item && before.TryGetValue(item, out var from) && c.TranslatePoint(default, ProjectCards) is { } to)
            {
                if (from != to)
                {
                    var card = c;
                    card.ZIndex = 1;
                    Controls.Glide.Play(card, from - to, done: () => card.ZIndex = 0);
                }
            }
            else
            {
                Controls.Entrance.Play(c);
            }
        }
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

    /// <summary>The model menu's Mode row: the modes open in a menu of their own beside it.</summary>
    private void OnModeRowClick(object? sender, RoutedEventArgs e) => FlyoutBase.ShowAttachedFlyout(ModeRow);

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

    // ------------------------------------------------------------------ image preview

    private List<(Avalonia.Media.Imaging.Bitmap Image, string Name)> _previews = [];
    private int _previewIndex;

    private bool IsPreviewOpen => ImagePreview.Classes.Contains("open");

    private void OnAttachmentImageTapped(object? sender, TappedEventArgs e)
    {
        if (sender is Control { DataContext: Infrastructure.Attachment { Preview: { } shown } } && Vm is { } vm)
        {
            var images = vm.Attachments.Where(a => a.Preview is not null).Select(a => (a.Preview!, a.Name)).ToList();
            OpenPreview(images, images.FindIndex(i => ReferenceEquals(i.Item1, shown)));
            e.Handled = true;
        }
    }

    private void OnSentImageTapped(object? sender, TappedEventArgs e)
    {
        if (sender is Control { DataContext: Avalonia.Media.Imaging.Bitmap shown } control
            && control.FindAncestorOfType<ItemsControl>()?.ItemsSource is IEnumerable<Avalonia.Media.Imaging.Bitmap> all)
        {
            var images = all.Select((b, i) => (b, $"Image {i + 1}")).ToList();
            OpenPreview(images, images.FindIndex(i => ReferenceEquals(i.b, shown)));
            e.Handled = true;
        }
    }

    // A file Claude sent: a click opens it in its default app; right-click also reveals it in Explorer.
    private void OnSentFileTapped(object? sender, TappedEventArgs e)
    {
        OpenSentFile(sender, reveal: false);
        e.Handled = true;
    }

    private void OnSentFileOpenClick(object? sender, RoutedEventArgs e) => OpenSentFile(sender, reveal: false);

    private void OnSentFileRevealClick(object? sender, RoutedEventArgs e) => OpenSentFile(sender, reveal: true);

    private static void OpenSentFile(object? sender, bool reveal)
    {
        if ((sender as Control)?.DataContext is not Infrastructure.Attachment { Path: { } path })
        {
            return;
        }

        try
        {
            Process.Start(reveal
                ? new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = false }
                : new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // Gone, or nothing is registered to open it.
        }
    }

    // An artifact Claude published: a click opens its claude.ai page in the browser.
    private void OnArtifactTapped(object? sender, TappedEventArgs e)
    {
        if ((sender as Control)?.DataContext is ArtifactItem artifact)
        {
            Markdown.LinkSupport.Open(artifact.Url);
            e.Handled = true;
        }
    }

    private void OnArtifactOpenClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is ArtifactItem artifact)
        {
            Markdown.LinkSupport.Open(artifact.Url);
        }
    }

    private async void OnArtifactCopyLinkClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is ArtifactItem artifact && TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(artifact.Url);
        }
    }

    private void OnArtifactOpenFileClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is ArtifactItem { Path: { } path })
        {
            try
            {
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose();
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                // Gone, or nothing is registered to open it.
            }
        }
    }

    private void OpenPreview(List<(Avalonia.Media.Imaging.Bitmap, string)> images, int index)
    {
        if (images.Count == 0)
        {
            return;
        }

        _previews = images;
        ShowPreview(Math.Max(0, index));
        ImagePreview.Classes.Add("open");
        ImagePreview.Focusable = true;
        ImagePreview.Focus();
    }

    private void ShowPreview(int index)
    {
        _previewIndex = (index + _previews.Count) % _previews.Count;
        var (image, name) = _previews[_previewIndex];
        PreviewImage.Source = image;

        // At most the session's size, less the margins and the caption.
        PreviewImage.MaxWidth = Math.Max(100, Bounds.Width - 96);
        PreviewImage.MaxHeight = Math.Max(100, Bounds.Height - 130);
        PreviewName.Text = name;
        var size = image.PixelSize;
        PreviewMeta.Text = $"{size.Width} × {size.Height}" + (_previews.Count > 1 ? $" · {_previewIndex + 1} of {_previews.Count}" : "");
    }

    /// <summary>A thumbnail's right-click menu (a sent image, or one attached to the composer).</summary>
    private void OnCopyImageClick(object? sender, RoutedEventArgs e)
    {
        var image = (sender as Control)?.DataContext switch
        {
            Avalonia.Media.Imaging.Bitmap bitmap => bitmap,
            Infrastructure.Attachment { Preview: { } preview } => preview,
            _ => null,
        };
        if (image is not null)
        {
            CopyImage(image);
        }
    }

    private void OnCopyPreviewClick(object? sender, RoutedEventArgs e)
    {
        CopyPreview();
        e.Handled = true;
    }

    private void CopyPreview()
    {
        if (_previews.Count > 0 && CopyImage(_previews[_previewIndex].Image))
        {
            // "Copied" for a moment, then back.
            PreviewCopyText.Text = "Copied";
            DispatcherTimer.RunOnce(() => PreviewCopyText.Text = "Copy image", TimeSpan.FromSeconds(1.5));
        }
    }

    private bool CopyImage(Avalonia.Media.Imaging.Bitmap image) =>
        TopLevel.GetTopLevel(this)?.TryGetPlatformHandle()?.Handle is { } owner && Infrastructure.PromptImages.CopyImage(image, owner);

    private void ClosePreview()
    {
        ImagePreview.Classes.Remove("open");
        ImagePreview.Focusable = false;
        FocusComposerSoon();
    }

    private void OnImagePreviewPressed(object? sender, PointerPressedEventArgs e)
    {
        ClosePreview();
        e.Handled = true;
    }

    /// <summary>While the preview is open, Esc (or Space, Enter) closes it and ← → step through the images.</summary>
    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (!IsPreviewOpen)
        {
            // A subagent's conversation is open: Esc goes back to the session.
            if (e.Key == Key.Escape && Vm is { HasOpenSubagent: true } session)
            {
                session.OpenSubagent = null;
                e.Handled = true;
            }

            return;
        }

        switch (e.Key)
        {
            case Key.Escape or Key.Space or Key.Enter:
                ClosePreview();
                break;
            case Key.Left:
                ShowPreview(_previewIndex - 1);
                break;
            case Key.Right:
                ShowPreview(_previewIndex + 1);
                break;
            case Key.C when e.KeyModifiers == KeyModifiers.Control:
                CopyPreview();
                break;
            default:
                return;
        }

        e.Handled = true;
    }

    private void OnSubagentLineTapped(object? sender, TappedEventArgs e)
    {
        if (sender is Control { DataContext: SubagentViewModel agent } && Vm is { } vm)
        {
            vm.OpenSubagent = agent;
        }
    }

    private void OnCloseSubagentClick(object? sender, RoutedEventArgs e)
    {
        if (Vm is { } vm)
        {
            vm.OpenSubagent = null;
        }

        FocusComposerSoon();
    }

    private void OnRemoveQueuedClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: QueuedPrompt prompt })
        {
            Vm?.RemoveQueuedCommand.Execute(prompt);
        }
    }

    private void OnEditQueuedClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: QueuedPrompt prompt })
        {
            BeginQueuedEdit(prompt);
        }
    }

    private void OnQueuedDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is Control { DataContext: QueuedPrompt prompt })
        {
            BeginQueuedEdit(prompt);
            e.Handled = true;
        }
    }

    private void OnSaveQueuedClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: QueuedPrompt prompt })
        {
            Vm?.EndQueuedEdit(prompt, save: true);
            FocusComposerSoon();
        }
    }

    /// <summary>Opens a queued prompt's text box, focused with the caret at the end.</summary>
    private void BeginQueuedEdit(QueuedPrompt prompt)
    {
        Vm?.EditQueued(prompt);
        Dispatcher.UIThread.Post(() =>
        {
            var box = QueueList.GetVisualDescendants().OfType<TextBox>()
                .FirstOrDefault(t => t.Classes.Contains("queuededit") && ReferenceEquals(t.DataContext, prompt));
            if (box is not null)
            {
                box.Focus();
                box.CaretIndex = box.Text?.Length ?? 0;
            }
        }, DispatcherPriority.Loaded);
    }

    /// <summary>
    /// Ctrl pressed while Claude works: "Send instantly" slides out of the send button. Any
    /// other key (a shortcut, Enter sending) takes it away again.
    /// </summary>
    private void OnComposerCtrlDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.LeftCtrl or Key.RightCtrl))
        {
            ShowSendInstantly(false);
            return;
        }

        ShowSendInstantly(Vm is { IsTurnRunning: true, ShowSendButton: true });
    }

    private void ShowSendInstantly(bool show)
    {
        var width = 0.0;
        if (show && SendInstantlyLabel.Child is { } text)
        {
            text.Measure(Size.Infinity);
            width = text.DesiredSize.Width;
        }

        SendInstantlyLabel.Width = width;
        SendInstantlyLabel.Opacity = show ? 1 : 0;
    }

    /// <summary>In a queued prompt's text box: Enter saves, Shift+Enter is a new line, Esc drops the edit.</summary>
    private void OnQueuedEditKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Source is not TextBox { DataContext: QueuedPrompt prompt } || Vm is not { } vm)
        {
            return;
        }

        if (e.Key == Key.Enter && (e.KeyModifiers & KeyModifiers.Shift) == 0)
        {
            vm.EndQueuedEdit(prompt, save: true);
        }
        else if (e.Key == Key.Escape)
        {
            vm.EndQueuedEdit(prompt, save: false);
        }
        else
        {
            return;
        }

        e.Handled = true;
        FocusComposerSoon();
    }

    /// <summary>Clicking elsewhere keeps what was typed.</summary>
    private void OnQueuedEditLostFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is TextBox { DataContext: QueuedPrompt { IsEditing: true } prompt })
        {
            Vm?.EndQueuedEdit(prompt, save: true);
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
        ShowDropHint(any);
    }

    private void ShowDropHint(bool on)
    {
        ComposerBar.Classes.Set("dragover", on);
        DropOverlay.Classes.Set("on", on);
    }

    // Images are attached as images, anything else (files, folders) as chips that send their paths.
    private void OnDrop(object? sender, DragEventArgs e)
    {
        ShowDropHint(false);
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

    private async void OnCopyResponseClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: AssistantTextItem response } && TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(response.Text);
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
