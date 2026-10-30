using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.TextFormatting;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Nomad.App.ViewModels;
using Nomad.App.Views;
using Nomad.Core.Agents;

namespace Nomad.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;

    private const int DwmwaCloak = 13;

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    private void HideShortcutHints() => _vm.ShowShortcutHints(false);

    public MainWindow(MainViewModel vm)
    {
        _vm = vm;
        DataContext = vm;
        InitializeComponent();
        LayoutUpdated += (_, _) => UpdateTitleArea();

        // A debug run (--dev-*) says so in the taskbar too: its own title and a gold tent.
        if (Program.IsDebugRun)
        {
            Title = "Nomad.ai (DEBUG)";
            if (Infrastructure.DebugIcon.Create() is { } icon)
            {
                Icon = icon;
            }
        }

        HookGitGlide();

        // A session done or waiting while the window is in the background: a notification and/or a flashing taskbar button.
        vm.TurnCompleted += session =>
        {
            if (session.Queued.Count == 0)
            {
                var reply = session.Items.OfType<AssistantTextItem>().LastOrDefault()?.Text;
                CallForAttention(session, string.IsNullOrWhiteSpace(reply) ? "Finished" : Nomad.Core.Sessions.SessionTitles.Shorten(reply, 140));
            }
        };
        vm.PermissionRequested += session => CallForAttention(
            session,
            session.PendingPermission is QuestionItem ? "Claude has a question for you" : session.PendingPermission?.Title ?? "Needs your answer");

        // The mouse's back and forward buttons go back and forward between sessions, as the title bar's arrows do.
        AddHandler(PointerPressedEvent, (_, e) =>
        {
            var button = e.GetCurrentPoint(this).Properties.PointerUpdateKind;
            var command = button switch
            {
                PointerUpdateKind.XButton1Pressed => vm.GoBackCommand,
                PointerUpdateKind.XButton2Pressed => vm.GoForwardCommand,
                _ => null,
            };
            if (command is not null)
            {
                if (command.CanExecute(null))
                {
                    command.Execute(null);
                }

                e.Handled = true;
            }
        }, RoutingStrategies.Tunnel, handledEventsToo: true);

        // Ctrl+B on the way down, so it works while the composer (a TextBox) has focus.
        AddHandler(KeyDownEvent, (_, e) =>
        {
            _clickModifiers = e.KeyModifiers;

            // The rename box sits in the row's button, which would take Enter and Space as a click: Enter saves, Esc keeps
            // the old name. Insert Space here because handling KeyDown suppresses Windows text input.
            if (e.Source is TextBox { DataContext: SessionListItemViewModel { IsRenaming: true } renaming } renameBox && e.Key is Key.Enter or Key.Escape or Key.Space)
            {
                if (e.Key != Key.Space)
                {
                    vm.EndRename(renaming, save: e.Key == Key.Enter);
                }
                else if (e.KeyModifiers is KeyModifiers.None or KeyModifiers.Shift)
                {
                    renameBox.SelectedText = " ";
                }

                e.Handled = true;
                return;
            }

            // The title bar's rename box sits in the title's button the same way.
            if (e.Source == TitleRenameBox && vm.IsTitleRenaming && e.Key is Key.Enter or Key.Escape or Key.Space)
            {
                if (e.Key != Key.Space)
                {
                    vm.EndTitleRename(save: e.Key == Key.Enter);
                    this.GetVisualDescendants().OfType<SessionView>().FirstOrDefault()?.FocusComposerSoon();
                }
                else if (e.KeyModifiers is KeyModifiers.None or KeyModifiers.Shift)
                {
                    TitleRenameBox.SelectedText = " ";
                }

                e.Handled = true;
                return;
            }

            // The unsaved-edits question takes every key: Enter saves, Esc stays.
            if (vm.Unsaved.IsOpen)
            {
                if (e.Key == Key.Escape)
                {
                    vm.Unsaved.Cancel();
                }
                else if (e.Key == Key.Enter)
                {
                    vm.Unsaved.SaveAllCommand.Execute(null);
                }

                e.Handled = true;
                return;
            }

            // Ctrl+T: in the Files view the code search, else the session search. While open they take Esc, the
            // arrows and Enter.
            var codeSearch = vm.Current.Files.CodeSearch;
            if (e.Key == Key.T && e.KeyModifiers == KeyModifiers.Control)
            {
                if (vm.Current.IsFilesView && !vm.Search.IsOpen && !vm.IsSettingsOpen)
                {
                    vm.Current.Files.FindInFiles.Close();
                    codeSearch.Toggle();
                }
                else
                {
                    codeSearch.Close();
                    vm.Search.Toggle();
                }

                e.Handled = true;
                return;
            }

            // Ctrl+Shift+F in the Files view: find in files, from the text selected in the file shown.
            var findInFiles = vm.Current.Files.FindInFiles;
            if (e.Key == Key.F && e.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Shift) && vm.Current.IsFilesView &&
                !vm.Search.IsOpen && !vm.IsSettingsOpen)
            {
                codeSearch.Close();
                string? selected = null;
                if (vm.Current.Files.ActiveGroup.ActiveTab?.Document is { } doc && doc.Anchor != doc.Caret && doc.Anchor.Line == doc.Caret.Line)
                {
                    selected = doc.GetText(doc.Anchor <= doc.Caret ? doc.Anchor : doc.Caret, doc.Anchor <= doc.Caret ? doc.Caret : doc.Anchor);
                }

                findInFiles.Open(selected);
                e.Handled = true;
                return;
            }

            if (findInFiles.IsOpen && e.KeyModifiers is KeyModifiers.None or KeyModifiers.Shift)
            {
                switch (e.Key)
                {
                    case Key.Escape: findInFiles.Close(); e.Handled = true; return;
                    case Key.Enter: findInFiles.OpenResult(); e.Handled = true; return;
                    case Key.Down: findInFiles.Move(1); e.Handled = true; return;
                    case Key.Up: findInFiles.Move(-1); e.Handled = true; return;
                    case Key.PageDown: findInFiles.Move(12); e.Handled = true; return;
                    case Key.PageUp: findInFiles.Move(-12); e.Handled = true; return;
                }
            }

            if (codeSearch.IsOpen && e.KeyModifiers is KeyModifiers.None or KeyModifiers.Shift)
            {
                switch (e.Key)
                {
                    case Key.Escape: codeSearch.Close(); e.Handled = true; return;
                    case Key.Enter: codeSearch.OpenResult(); e.Handled = true; return;
                    case Key.Down: codeSearch.Move(1); e.Handled = true; return;
                    case Key.Up: codeSearch.Move(-1); e.Handled = true; return;
                    case Key.PageDown: codeSearch.Move(12); e.Handled = true; return;
                    case Key.PageUp: codeSearch.Move(-12); e.Handled = true; return;
                }
            }

            // In the Files view: Ctrl+F and Ctrl+H find and replace in the file shown, F3 and Shift+F3 go to the next
            // and previous match.
            if (vm.Current.IsFilesView && !vm.Search.IsOpen && !vm.IsSettingsOpen && !codeSearch.IsOpen && !findInFiles.IsOpen &&
                vm.Current.Files.ActiveGroup is { HasTabs: true } findGroup)
            {
                if (e.Key is Key.F or Key.H && e.KeyModifiers == KeyModifiers.Control)
                {
                    findGroup.Find.Open(replace: e.Key == Key.H);
                    e.Handled = true;
                    return;
                }

                // Ctrl+G: go to a line, as ":" and its number in the code search.
                if (e.Key == Key.G && e.KeyModifiers == KeyModifiers.Control)
                {
                    codeSearch.Open(":");
                    e.Handled = true;
                    return;
                }

                if (e.Key == Key.F3 && findGroup.Find.IsOpen && e.KeyModifiers is KeyModifiers.None or KeyModifiers.Shift)
                {
                    if (e.KeyModifiers == KeyModifiers.Shift)
                    {
                        findGroup.Find.Previous();
                    }
                    else
                    {
                        findGroup.Find.Next();
                    }

                    e.Handled = true;
                    return;
                }
            }

            // Ctrl+- and Ctrl+Shift+-: back and forward between the places jumped to in the Files view.
            if (vm.Current.IsFilesView && e.Key is Key.OemMinus or Key.Subtract &&
                e.KeyModifiers is KeyModifiers.Control or (KeyModifiers.Control | KeyModifiers.Shift))
            {
                if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
                {
                    vm.Current.Files.NavigateForward();
                }
                else
                {
                    vm.Current.Files.NavigateBack();
                }

                e.Handled = true;
                return;
            }

            // Ctrl+F: find in the chat.
            if (e.Key == Key.F && e.KeyModifiers == KeyModifiers.Control && !vm.Search.IsOpen && !vm.IsSettingsOpen)
            {
                e.Handled = this.GetVisualDescendants().OfType<SessionView>().FirstOrDefault()?.OpenFind() == true;
                return;
            }

            if (vm.Search.IsOpen && e.KeyModifiers is KeyModifiers.None or KeyModifiers.Shift)
            {
                switch (e.Key)
                {
                    case Key.Escape: vm.Search.Close(); e.Handled = true; return;
                    case Key.Enter: vm.Search.OpenResult(); e.Handled = true; return;
                    case Key.Down: vm.Search.Move(1); e.Handled = true; return;
                    case Key.Up: vm.Search.Move(-1); e.Handled = true; return;
                    case Key.PageDown: vm.Search.Move(8); e.Handled = true; return;
                    case Key.PageUp: vm.Search.Move(-8); e.Handled = true; return;
                }
            }

            // Esc lets the sidebar's picked rows go (and otherwise does what it did).
            if (e.Key == Key.Escape && e.KeyModifiers == KeyModifiers.None && vm.ClearPicks())
            {
                e.Handled = true;
                return;
            }

            // F1…F4: the model of a new session (OpenCode: a favorite), when turned on in the settings.
            if (vm.Current.ModelFunctionKeys && !vm.IsSettingsOpen && e.KeyModifiers == KeyModifiers.None && e.Key is >= Key.F1 and <= Key.F4 &&
                !(e.Key == Key.F2 && e.Source is Ide.FileTreeView))
            {
                vm.Current.SelectShortcutModel(e.Key - Key.F1);
                e.Handled = true;
                return;
            }

            // (In the Files view's tree, F2 renames the file instead.)
            if (e.Key == Key.F2 && e.KeyModifiers == KeyModifiers.None && vm.CurrentRow is { } row && e.Source is not Ide.FileTreeView)
            {
                BeginRename(row);
                e.Handled = true;
                return;
            }

            if (e.Key == Key.B && e.KeyModifiers == KeyModifiers.Control)
            {
                vm.ToggleSidebar();
                e.Handled = true;
            }
            else if (e.Key == Key.B && e.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Shift))
            {
                vm.ToggleSidePanel();
                e.Handled = true;
            }
            else if (vm.FilesViewGesture is { } filesKey && filesKey.Matches(e) && FilesKeyApplies(vm, e))
            {
                vm.Current.ToggleFilesView();
                e.Handled = true;
            }

            // Ctrl+1…9 opens the sidebar's rows top to bottom; holding Ctrl shows their numbers.
            if (e.KeyModifiers == KeyModifiers.Control && e.Key is >= Key.D1 and <= Key.D9 or >= Key.NumPad1 and <= Key.NumPad9)
            {
                var number = e.Key >= Key.NumPad1 ? e.Key - Key.NumPad1 + 1 : e.Key - Key.D1 + 1;
                e.Handled = vm.OpenByShortcut(number);
            }
            else if (e.Key is Key.LeftCtrl or Key.RightCtrl)
            {
                vm.ShowShortcutHints(true);
            }
            else
            {
                HideShortcutHints(); // Ctrl+C and the like: no numbers.
            }
        }, RoutingStrategies.Tunnel);
        AddHandler(KeyUpEvent, (_, e) =>
        {
            if (e.Key is Key.LeftCtrl or Key.RightCtrl)
            {
                HideShortcutHints();
            }
        }, RoutingStrategies.Tunnel);
        Deactivated += (_, _) => HideShortcutHints();

        // A row's Click carries no modifiers: Ctrl or Shift held at the press (or key) picks rows instead of opening one.
        AddHandler(PointerPressedEvent, (_, e) => _clickModifiers = e.KeyModifiers, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerReleasedEvent, (_, e) => _clickModifiers = e.KeyModifiers, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(ContextRequestedEvent, OnContextRequested, RoutingStrategies.Tunnel);

        // A session row dragged onto the zone under New is pinned (a pinned one unpinned).
        AddHandler(PointerPressedEvent, OnRowDragPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerMovedEvent, OnRowDragMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerReleasedEvent, OnRowDragReleased, RoutingStrategies.Tunnel, handledEventsToo: true);
        Sidebar.PointerCaptureLost += (_, _) => EndRowDrag(drop: false);

        var settings = vm.Settings;
        if (vm.Dev.Size is { } size)
        {
            Width = size.Width;
            Height = size.Height;
        }
        else
        {
            Width = Math.Max(MinWidth, settings.WindowWidth);
            Height = Math.Max(MinHeight, settings.WindowHeight);
            if (settings.WindowMaximized)
            {
                WindowState = WindowState.Maximized;
            }
        }

        // A screenshot run renders the window to a bitmap, so the window itself stays invisible: DWM
        // cloaks it before it is first shown (Windows moves off-screen windows back), and it never
        // takes focus or shows in the taskbar.
        if (vm.Dev.ScreenshotPath is not null)
        {
            WindowState = WindowState.Normal;
            ShowActivated = false;
            ShowInTaskbar = false;
            if (TryGetPlatformHandle() is { } handle)
            {
                var cloak = 1;
                _ = DwmSetWindowAttribute(handle.Handle, DwmwaCloak, ref cloak, sizeof(int));
            }
        }

        Opened += (_, _) => _ = StartAsync();
        Closing += OnClosing;

        // While the unsaved-edits question is open, typing doesn't reach the files behind it.
        AddHandler(TextInputEvent, (_, e) => e.Handled |= vm.Unsaved.IsOpen, RoutingStrategies.Tunnel);

        vm.Search.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SessionSearchViewModel.IsOpen))
            {
                if (vm.Search.IsOpen)
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        SearchBox.Focus();
                        SearchBox.SelectAll();
                    }, DispatcherPriority.Loaded);
                }
                else
                {
                    this.GetVisualDescendants().OfType<SessionView>().FirstOrDefault()?.FocusComposerSoon();
                }
            }
            else if (e.PropertyName == nameof(SessionSearchViewModel.SelectedIndex) && vm.Search.SelectedIndex is >= 0 and var index)
            {
                // The arrows keep the selected row in sight (the first one scrolls back to the top).
                Dispatcher.UIThread.Post(() =>
                {
                    if (index == 0)
                    {
                        SearchScroll.Offset = default;
                    }
                    else
                    {
                        SearchList.ScrollIntoView(index);
                    }
                }, DispatcherPriority.Loaded);
            }
        };
        CreateTrayIcon();
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.IsSettingsOpen) && vm.IsSettingsOpen)
            {
                Dispatcher.UIThread.Post(() => SettingsHost.Focus(), DispatcherPriority.Loaded);
            }
        };
    }

    private void OnSearchOverlayPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _vm.Search.Close();
        e.Handled = true;
    }

    private void OnSearchResultClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: SessionSearchResult result })
        {
            _vm.Search.OpenResult(result);
        }
    }

    // A row that joins the results fades in (those that stay keep their place, and rows scrolled to just appear).
    private void OnSearchRowPrepared(object? sender, ContainerPreparedEventArgs e)
    {
        if (_vm.Search.IsRaised && _vm.Search.Results.ElementAtOrDefault(e.Index) is { IsFresh: true })
        {
            Nomad.App.Controls.Entrance.Fade(e.Container, this);
        }
    }

    private void OnOverlayPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _vm.CloseSettingsCommand.Execute(null);
        e.Handled = true;
    }

    // ------------------------------------------------------------------ title bar

    private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        if (e.ClickCount == 2)
        {
            ToggleMaximize();
        }
        else
        {
            BeginMoveDrag(e);
        }

        e.Handled = true;
    }

    private void OnMinimizeClick(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximizeClick(object? sender, RoutedEventArgs e) => ToggleMaximize();

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();

    private void OnExitClick(object? sender, RoutedEventArgs e) => Exit();

    /// <summary>Quits for real (the app menu's Exit, the tray's, an update's restart), even with "close to tray" on.</summary>
    public void Exit()
    {
        _exiting = true;
        Close();
    }

    private bool _exiting;

    private void ToggleMaximize() =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == WindowStateProperty || change.Property == OffScreenMarginProperty)
        {
            // A maximised window with an extended client area hangs over the screen edges.
            Padding = WindowState == WindowState.Maximized ? OffScreenMargin : default;
            if (MaximizeIcon is not null && this.TryFindResource(WindowState == WindowState.Maximized ? "IconCaptionRestore" : "IconCaptionMax", out var icon) && icon is Geometry g)
            {
                MaximizeIcon.Data = g;
            }
        }
    }

    // ------------------------------------------------------------------ sidebar and menus

    // The modifiers of the last press or key, for the clicks that follow (a Click carries none of its own).
    private KeyModifiers _clickModifiers;

    // Ctrl+click picks a row, Shift+click a run of them (for the context menu); a plain click opens it.
    private void OnRecentClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: SessionListItemViewModel item })
        {
            return;
        }

        if (_clickModifiers.HasFlag(KeyModifiers.Shift))
        {
            _vm.PickRange(item);
        }
        else if (_clickModifiers.HasFlag(KeyModifiers.Control))
        {
            _vm.TogglePick(item);
        }
        else
        {
            _vm.ClearPicks();
            _ = _vm.OpenSessionAsync(item);
        }
    }

    // ------------------------------------------------------------------ drag to pin

    // The row pressed (and where), until the press ends; dragging once it has moved far enough.
    private (SessionListItemViewModel Item, Point From)? _rowPress;
    private bool _rowDragging;

    private void OnRowDragPressed(object? sender, PointerPressedEventArgs e)
    {
        _rowPress = null;
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed && e.KeyModifiers == KeyModifiers.None
            && (e.Source as Visual)?.GetSelfAndVisualAncestors().OfType<Button>().FirstOrDefault(b => b.Classes.Contains("sessionrow"))
                is { DataContext: SessionListItemViewModel { IsRenaming: false } item })
        {
            _rowPress = (item, e.GetPosition(this));
        }
    }

    private void OnRowDragMoved(object? sender, PointerEventArgs e)
    {
        if (_rowPress is not { } press)
        {
            return;
        }

        var at = e.GetPosition(this);
        if (!_rowDragging)
        {
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            {
                _rowPress = null;
                return;
            }

            if (Math.Abs(at.X - press.From.X) < 6 && Math.Abs(at.Y - press.From.Y) < 6)
            {
                return;
            }

            // The sidebar takes the pointer from the row, so letting go is no click on it.
            _rowDragging = true;
            e.Pointer.Capture(Sidebar);
            press.Item.IsDragged = true;
            PinDropText.Text = press.Item.IsPinned ? "Drop to unpin" : "Drop to pin";
            DragGhostText.Text = press.Item.Title;
            DragGhost.Opacity = 0.95;
            Controls.Fold.SetIsOpen(PinDropHost, true);
        }

        Canvas.SetLeft(DragGhost, at.X + 12);
        Canvas.SetTop(DragGhost, at.Y + 8);
        PinDropZone.Classes.Set("over", OverPinDropZone(e));
        e.Handled = true;
    }

    private void OnRowDragReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_rowDragging)
        {
            var drop = OverPinDropZone(e);
            EndRowDrag(drop);
            e.Pointer.Capture(null);
            e.Handled = true;
        }

        _rowPress = null;
    }

    // A little around the zone counts, so the drop doesn't have to be exact.
    private bool OverPinDropZone(PointerEventArgs e) =>
        PinDropZone.IsEffectivelyVisible && new Rect(PinDropZone.Bounds.Size).Inflate(6).Contains(e.GetPosition(PinDropZone));

    private void EndRowDrag(bool drop)
    {
        if (!_rowDragging || _rowPress is not { } press)
        {
            return;
        }

        _rowDragging = false;
        _rowPress = null;
        press.Item.IsDragged = false;
        DragGhost.Opacity = 0;
        PinDropZone.Classes.Remove("over");
        Controls.Fold.SetIsOpen(PinDropHost, false);
        if (drop)
        {
            _ = _vm.TogglePinnedAsync(press.Item);
        }
    }

    // ------------------------------------------------------------------ files dragged from the Files view's tree

    /// <summary>
    /// A file dragged out of the Files view's tree: its name follows the pointer, and let go over the title bar's Chat
    /// switch it is attached to the chat, which comes up. A null <paramref name="e"/> ends the drag without a drop.
    /// </summary>
    public void DragFile(SessionViewModel session, string path, PointerEventArgs? e, bool done)
    {
        var over = e is not null && ChatSwitchButton.IsEffectivelyVisible &&
                   new Rect(ChatSwitchButton.Bounds.Size).Inflate(6).Contains(e.GetPosition(ChatSwitchButton));
        if (e is null || done)
        {
            DragGhost.Opacity = 0;
            ChatSwitchButton.Classes.Remove("droptarget");
            if (done && over)
            {
                session.AddAttachments([Infrastructure.PromptImages.FromPath(path)]);
                session.ShowChatCommand.Execute(null);
            }

            return;
        }

        var at = e.GetPosition(this);
        DragGhostText.Text = Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
        DragGhost.Opacity = 0.95;
        Canvas.SetLeft(DragGhost, at.X + 12);
        Canvas.SetTop(DragGhost, at.Y + 8);
        ChatSwitchButton.Classes.Set("droptarget", over);
    }

    // A row's GitHub icons open their pull request or issue (and keep the press from opening the session).
    private void OnPullRequestIconPressed(object? sender, PointerPressedEventArgs e) =>
        OpenLink(e, (sender as Control)?.DataContext is SessionListItemViewModel { PullRequest.Url: { } url } ? url : null);

    private void OnIssueIconPressed(object? sender, PointerPressedEventArgs e) =>
        OpenLink(e, (sender as Control)?.DataContext is SessionListItemViewModel { Issue.Url: { } url } ? url : null);

    private void OpenLink(PointerPressedEventArgs e, string? url)
    {
        if (url is null || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        e.Handled = true;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // No browser registered.
        }
    }

    private void OnRenameSessionClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: SessionListItemViewModel item })
        {
            BeginRename(item);
        }
    }

    private void OnSessionRowDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (_clickModifiers == KeyModifiers.None && sender is Control { DataContext: SessionListItemViewModel { IsRenaming: false } item })
        {
            BeginRename(item);
            e.Handled = true;
        }
    }

    /// <summary>Puts the row's title in a text box, focused and all selected.</summary>
    private void BeginRename(SessionListItemViewModel item)
    {
        _vm.BeginRename(item);
        Dispatcher.UIThread.Post(() =>
        {
            var box = Sidebar.GetVisualDescendants().OfType<TextBox>()
                .FirstOrDefault(t => t.Classes.Contains("rename") && ReferenceEquals(t.DataContext, item));
            if (box is not null)
            {
                box.Focus();
                box.SelectAll();
            }
        }, DispatcherPriority.Loaded);
    }

    /// <summary>A click on the title bar's title edits it in place (Enter saves, Esc keeps it, clicking away saves).</summary>
    private void OnTitleClick(object? sender, RoutedEventArgs e)
    {
        if (_vm.IsTitleRenaming || !_vm.BeginTitleRename())
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            TitleRenameBox.Focus();
            TitleRenameBox.SelectAll();
        }, DispatcherPriority.Loaded);
    }

    private void OnTitleRenameLostFocus(object? sender, RoutedEventArgs e) => _vm.EndTitleRename(save: true);

    /// <summary>Clicking elsewhere keeps the new name.</summary>
    private void OnRenameLostFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is TextBox { DataContext: SessionListItemViewModel { IsRenaming: true } item })
        {
            _vm.EndRename(item, save: true);
        }
    }

    private void OnToggleAliveClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: SessionListItemViewModel item })
        {
            var targets = _vm.MenuTargets(item);
            _ = targets.Count > 1 ? _vm.SetAliveAsync(targets, !item.IsAlive) : _vm.ToggleAliveAsync(item);
        }
    }

    // ------------------------------------------------------------------ git chips between the status row and title bar

    // A new session shows its branch and worktree switch under the composer; once started (or opening another session)
    // they sit in the title bar. Crossing over, they slide and fade into the new place.
    private SessionViewModel? _gitWatched;
    private (Rect? Branch, Rect? Worktree) _gitBefore;

    private void HookGitGlide()
    {
        _vm.PropertyChanging += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.Current))
            {
                CaptureGit();
            }
        };
        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.Current))
            {
                WatchGit(_vm.Current);
                GlideGitSoon();
            }
        };
        WatchGit(_vm.Current);
    }

    private void WatchGit(SessionViewModel session)
    {
        if (_gitWatched is { } old)
        {
            old.PropertyChanging -= OnGitSessionChanging;
            old.PropertyChanged -= OnGitSessionChanged;
        }

        _gitWatched = session;
        session.PropertyChanging += OnGitSessionChanging;
        session.PropertyChanged += OnGitSessionChanged;
    }

    private void OnGitSessionChanging(object? sender, System.ComponentModel.PropertyChangingEventArgs e)
    {
        if (e.PropertyName == nameof(SessionViewModel.IsNew))
        {
            CaptureGit();
        }
    }

    private void OnGitSessionChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SessionViewModel.IsNew))
        {
            GlideGitSoon();
        }
    }

    private (Control? Branch, Control? Worktree) StatusGit() =>
        this.GetVisualDescendants().OfType<SessionView>().FirstOrDefault() is { } view ? view.GitChips : default;

    private Rect? Where(Control? control) =>
        control is { IsEffectivelyVisible: true, Bounds.Width: > 0 } && control.TranslatePoint(default, this) is { } p
            ? new Rect(p, control.Bounds.Size)
            : null;

    private void CaptureGit()
    {
        var (branch, worktree) = StatusGit();
        _gitBefore = (Where(TitleBranch) ?? Where(branch), Where(TitleWorktree) ?? Where(worktree));
    }

    /// <summary>After the change is laid out (before it renders), each chip that moved between rows slides into its new place.</summary>
    private void GlideGitSoon()
    {
        var before = _gitBefore;
        _gitBefore = default;
        if (before is (null, null))
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            UpdateLayout();
            var (branch, worktree) = StatusGit();
            Move(before.Branch, Where(TitleBranch) is not null ? TitleBranch : branch);
            Move(before.Worktree, Where(TitleWorktree) is not null ? TitleWorktree : worktree);
        }, DispatcherPriority.Normal);

        void Move(Rect? from, Control? target)
        {
            if (from is not { } old || Where(target) is not { } now || Math.Abs(old.Y - now.Y) < 20)
            {
                return;
            }

            // Into the title bar it slides down from above; into the status row, up from below.
            var intoTitle = target!.GetVisualAncestors().Contains(TitleBar);
            Controls.Glide.Play(target, new Vector(0, intoTitle ? -8 : 8), 220, fade: true);
        }
    }

    // ------------------------------------------------------------------ sidebar and side panel resizing

    private double? _gripOffset;
    private Border? _gripPane;
    private Avalonia.Animation.Transitions? _paneTransitions;

    private void OnSidebarGripPressed(object? sender, PointerPressedEventArgs e) =>
        BeginGripDrag(e, SidebarGrip, Sidebar, _vm.SidebarWidth - e.GetPosition(this).X, () =>
            _vm.SidebarWidth = 375);

    private void OnSidebarGripMoved(object? sender, PointerEventArgs e)
    {
        if (_gripOffset is { } offset && _gripPane == Sidebar)
        {
            _vm.SidebarWidth = e.GetPosition(this).X + offset;
        }
    }

    private void OnSidebarGripReleased(object? sender, PointerReleasedEventArgs e) => EndGripDrag(e.Pointer);

    // The side panel grows to the left: its width is measured from the window's right edge.
    private void OnSidePanelGripPressed(object? sender, PointerPressedEventArgs e) =>
        BeginGripDrag(e, SidePanelGrip, SidePanel, _vm.SidePanelWidth + e.GetPosition(this).X, () =>
            _vm.SidePanelWidth = MainViewModel.DefaultSidePanelWidth);

    private void OnSidePanelGripMoved(object? sender, PointerEventArgs e)
    {
        if (_gripOffset is { } offset && _gripPane == SidePanel)
        {
            _vm.SidePanelWidth = offset - e.GetPosition(this).X;
        }
    }

    private void OnSidePanelGripReleased(object? sender, PointerReleasedEventArgs e) => EndGripDrag(e.Pointer);

    private void OnGripCaptureLost(object? sender, PointerCaptureLostEventArgs e) => EndGripDrag(null);

    private (string? Text, Typeface Typeface, double FontSize)? _titleMeasure;
    private double _titleTextWidth;
    private double? _viewSwitchLeft;

    /// <summary>Keep the switch centered, moving only far enough to clear the title and Git status.</summary>
    private void UpdateTitleArea()
    {
        var max = double.PositiveInfinity;
        if (ViewSwitch.IsVisible && ViewSwitch.Bounds.Width > 0
            && TitleBar.TranslatePoint(default, WindowContent) is { } titleAt
            && TitleButtons.TranslatePoint(default, WindowContent) is { } buttonsAt)
        {
            // Measure the untrimmed title, independent of the space the switch currently leaves it. Using the
            // constrained DesiredSize here would make a long title chase the moving switch.
            var text = _vm.IsTitleRenaming ? TitleRenameBox.Text : SessionTitleText.Text;
            var typeface = new Typeface(SessionTitleText.FontFamily, SessionTitleText.FontStyle,
                SessionTitleText.FontWeight, SessionTitleText.FontStretch);
            var measure = (text, typeface, SessionTitleText.FontSize);
            if (_titleMeasure is not { } previous || !previous.Equals(measure))
            {
                using var layout = new TextLayout(text ?? "", typeface, SessionTitleText.FontSize, null);
                _titleTextWidth = layout.WidthIncludingTrailingWhitespace;
                _titleMeasure = measure;
            }

            var children = TitleContent.Children.Where(c => c.IsVisible).ToArray();
            var titleWidth = SessionTitleButton.IsVisible
                ? Math.Min(SessionTitleButton.MaxWidth, Math.Max(_vm.IsTitleRenaming ? TitleRenameBox.MinWidth : 0,
                    _titleTextWidth) + SessionTitleButton.Padding.Left + SessionTitleButton.Padding.Right)
                : 0;
            var naturalWidth = TitleNavigation.DesiredSize.Width + TitleContent.Margin.Left + TitleContent.Margin.Right
                + titleWidth + children.Where(c => c != SessionTitleButton).Sum(c => c.DesiredSize.Width)
                + (TitleContent.ColumnDefinitions.Count - 1) * TitleContent.ColumnSpacing;
            var centered = Math.Floor((WindowContent.Bounds.Width - ViewSwitch.Bounds.Width) / 2);
            var rightmost = Math.Floor(buttonsAt.X - ViewSwitch.Bounds.Width - 12);
            var clearOfTitle = Math.Ceiling(titleAt.X + naturalWidth + 12);
            var left = Math.Max(0, Math.Min(rightmost, Math.Max(centered, clearOfTitle)));
            var first = _viewSwitchLeft is null;
            if (_viewSwitchLeft != left)
            {
                _viewSwitchLeft = left;
                // A newly shown switch starts in place; later moves retarget the running transition.
                var transitions = ViewSwitch.Transitions;
                if (first)
                {
                    ViewSwitch.Transitions = null;
                }

                ViewSwitch.Margin = new Thickness(left, 4, 0, 0);
                ViewSwitch.Transitions = transitions;
            }

            // Follow the animated position so the title and its metadata never run under the moving switch.
            var currentLeft = first ? left : ViewSwitch.Bounds.X;
            max = Math.Max(0, Math.Floor(currentLeft - titleAt.X) - 12);
        }
        else if (!ViewSwitch.IsVisible)
        {
            _viewSwitchLeft = null;
        }

        if (TitleArea.MaxWidth != max)
        {
            TitleArea.MaxWidth = max;
        }
    }

    /// <summary>
    /// The Chat / Files key (Tab by default) switches only where it takes nothing away: not in a dialog, not in a text
    /// box other than the composer, and not while the composer's Tab completes a "/" command or takes a suggestion.
    /// </summary>
    private static bool FilesKeyApplies(MainViewModel vm, KeyEventArgs e)
    {
        var session = vm.Current;
        if (!session.CanShowFiles || vm.IsSettingsOpen || vm.Search.IsOpen)
        {
            return false;
        }

        if (e.Source is TextBox box)
        {
            return box.Name == "Composer" && !session.IsCommandMenuOpen && !(session.HasSuggestion && e.KeyModifiers == KeyModifiers.None);
        }

        // Typing in a file: Tab (and Shift+Tab) indent there.
        if (e.Source is Nomad.App.Ide.CodeView { IsEditable: true } && (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Alt)) == 0)
        {
            return false;
        }

        return true;
    }

    /// <summary>Starts dragging a pane's edge (a double-click resets its width instead).</summary>
    private void BeginGripDrag(PointerPressedEventArgs e, Border grip, Border pane, double offset, Action reset)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        e.Handled = true;
        if (e.ClickCount == 2)
        {
            reset();
            _vm.SaveSettings();
            return;
        }

        // The width follows the pointer at once: the open/close slide would make it lag behind.
        _paneTransitions = pane.Transitions;
        pane.Transitions = null;
        _gripPane = pane;
        _gripOffset = offset;
        grip.Classes.Add("dragging");
        e.Pointer.Capture(grip);
    }

    private void EndGripDrag(IPointer? pointer)
    {
        if (_gripOffset is null || _gripPane is not { } pane)
        {
            return;
        }

        _gripOffset = null;
        _gripPane = null;
        pointer?.Capture(null);
        pane.Transitions = _paneTransitions;
        SidebarGrip.Classes.Remove("dragging");
        SidePanelGrip.Classes.Remove("dragging");
        _vm.SaveSettings();
    }

    private void OnToggleArchivedClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: SessionListItemViewModel item })
        {
            _ = _vm.SetArchivedAsync(_vm.MenuTargets(item), !item.IsArchived);
        }
    }

    private void OnTogglePinnedClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: SessionListItemViewModel item })
        {
            _ = _vm.TogglePinnedAsync(item);
        }
    }

    private void OnDeleteSessionClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: SessionListItemViewModel item })
        {
            _ = _vm.DeleteSessionsAsync(_vm.MenuTargets(item));
        }
    }

    // A row's menu names what it acts on: the row, or every picked row when it is one of them.
    // On the way down, before the row opens its menu (whose DataContext is only set once it is open).
    private void OnContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if ((e.Source as Visual)?.GetSelfAndVisualAncestors().OfType<Button>().FirstOrDefault(b => b.Classes.Contains("sessionrow"))
            is { DataContext: SessionListItemViewModel item, ContextMenu: { } menu })
        {
            PrepareSessionMenu(menu, item);
        }
        else if ((e.Source as Visual)?.GetSelfAndVisualAncestors().OfType<Button>().FirstOrDefault(b => b.Classes.Contains("groupheader"))
            is { DataContext: SessionGroupHeader header, ContextMenu: { } groupMenu })
        {
            if (header is not { CanPin: true, Folder: { } folder })
            {
                e.Handled = true; // Date, Pinned and "No folder" groups have no menu.
                return;
            }

            foreach (var entry in groupMenu.Items.OfType<MenuItem>())
            {
                entry.Header = _vm.IsAliveFolder(folder) ? "Unpin from Alive" : "Pin to Alive";
            }
        }
    }

    private void PrepareSessionMenu(ContextMenu menu, SessionListItemViewModel item)
    {

        var targets = _vm.MenuTargets(item);
        var many = targets.Count > 1;
        var count = many ? $" {targets.Count} sessions" : "";
        var manageable = targets.Any(s => s.CanManage);
        foreach (var entry in menu.Items.OfType<MenuItem>())
        {
            switch (entry.Tag)
            {
                case "rename" or "pin":
                    entry.IsEnabled = !many;
                    break;
                case "alive":
                    entry.Header = many ? $"Make{count} {(item.IsAlive ? "idle" : "alive")}" : item.AliveToggleText;
                    entry.IsEnabled = manageable;
                    break;
                case "archive":
                    entry.Header = (item.IsArchived ? "Unarchive" : "Archive") + count;
                    entry.IsEnabled = item.IsArchived || manageable;
                    break;
                case "delete":
                    entry.Header = "Delete" + count;
                    entry.IsEnabled = manageable;
                    break;
            }
        }
    }

    // The list options menu stays open, so the list can be watched regrouping behind it.
    private void OnGroupingClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: MenuOption option })
        {
            _vm.SetGrouping(option.Id);
        }
    }

    private void OnShowArchivedClick(object? sender, RoutedEventArgs e) => _vm.ShowArchived = !_vm.ShowArchived;

    private void OnAliveGroupingClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: MenuOption option })
        {
            _vm.SetAliveGrouping(option.Id);
        }
    }

    private void OnShowAliveProjectClick(object? sender, RoutedEventArgs e) => _vm.ShowAliveProject = !_vm.ShowAliveProject;

    private void OnGroupHeaderClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: SessionGroupHeader header })
        {
            _ = _vm.ToggleGroupAsync(header);
        }
    }

    private void OnGroupNewClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: SessionGroupHeader header })
        {
            _vm.NewSessionIn(header.Folder);
        }
    }

    private void OnPinFolderClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: SessionGroupHeader { Folder: { } folder } })
        {
            _vm.ToggleAliveFolder(folder);
        }
    }

    private async void OnPinAliveFolderClick(object? sender, RoutedEventArgs e)
    {
        AliveOptionsButton.Flyout?.Hide();
        var result = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Pin a folder to Alive",
            AllowMultiple = false,
        });

        if (result.Count > 0 && result[0].TryGetLocalPath() is { } path && !_vm.IsAliveFolder(path))
        {
            _vm.ToggleAliveFolder(path);
        }
    }

    private void OnMenuItemClick(object? sender, RoutedEventArgs e) => AppMenuButton.Flyout?.Hide();

    private void OnSearchMenuClick(object? sender, RoutedEventArgs e)
    {
        AppMenuButton.Flyout?.Hide();
        _vm.Search.Open();
    }

    /// <summary>The update chip: a downloaded update restarts at once, a found one opens the prompt with its notes.</summary>
    private void OnUpdateChipClick(object? sender, RoutedEventArgs e)
    {
        if (_vm.Updates.ShowRestartChip)
        {
            _vm.Updates.RestartNowCommand.Execute(null);
        }
        else
        {
            FlyoutBase.ShowAttachedFlyout(UpdateChip);
        }
    }

    private void OnUpdateLaterClick(object? sender, RoutedEventArgs e) => FlyoutBase.GetAttachedFlyout(UpdateChip)?.Hide();


    // ------------------------------------------------------------------ tray

    private TrayIcon? _tray;

    /// <summary>
    /// With "close to tray" on, an icon in the notification area: a click shows the window, its menu exits. A debug run
    /// (--dev-*) never goes to the tray: it has no icon, and its X quits.
    /// </summary>
    private void CreateTrayIcon()
    {
        if (Program.IsDebugRun || Application.Current is not { } app)
        {
            return;
        }

        var open = new NativeMenuItem("Open Nomad.ai");
        open.Click += (_, _) => ShowFromTray();
        var exit = new NativeMenuItem("Exit");
        exit.Click += (_, _) => Exit();
        _tray = new TrayIcon
        {
            Icon = Icon,
            ToolTipText = "Nomad.ai",
            IsVisible = _vm.Settings.CloseToTray,
            Menu = new NativeMenu { Items = { open, new NativeMenuItemSeparator(), exit } },
        };
        _tray.Clicked += (_, _) => ShowFromTray();
        TrayIcon.SetIcons(app, [_tray]);
        _vm.CloseToTrayChanged += () =>
        {
            _tray.IsVisible = _vm.Settings.CloseToTray;
            if (!_vm.Settings.CloseToTray && !IsVisible)
            {
                ShowFromTray(); // Never leave a hidden window without its icon.
            }
        };
    }

    /// <summary>Nomad.ai was launched again: this window shows, restored and in front (on a notification's session).</summary>
    public void BringToFront(string? sessionId)
    {
        ShowFromTray();
        if (sessionId is not null)
        {
            _ = _vm.OpenSessionByIdAsync(sessionId);
        }
    }

    /// <summary>Only while Nomad.ai is not the window in front (minimized, in the tray or behind another app).</summary>
    private void CallForAttention(SessionViewModel session, string text)
    {
        if (IsActive && IsVisible && WindowState != WindowState.Minimized)
        {
            return;
        }

        if (_vm.Settings.NotifyWhenDone)
        {
            Infrastructure.WindowsToast.Show(string.IsNullOrWhiteSpace(session.Title) ? "Nomad.ai" : session.Title, text, session.SessionId);
        }

        if (_vm.Settings.FlashTaskbar && IsVisible && TryGetPlatformHandle()?.Handle is { } hwnd)
        {
            // Flashes until the window comes to the front.
            var info = new FlashInfo { Size = (uint)Marshal.SizeOf<FlashInfo>(), Window = hwnd, Flags = 0x2 | 0xC, Count = uint.MaxValue };
            FlashWindowEx(ref info);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FlashInfo
    {
        public uint Size;
        public IntPtr Window;
        public uint Flags;
        public uint Count;
        public uint Timeout;
    }

    [DllImport("user32.dll")]
    private static extern bool FlashWindowEx(ref FlashInfo info);

    private void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized)
        {
            WindowState = _vm.Settings.WindowMaximized ? WindowState.Maximized : WindowState.Normal;
        }

        Activate();
    }
    // ------------------------------------------------------------------ lifetime

    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        // "Close to tray": the window only hides (the tray icon brings it back); Exit quits.
        if (_vm.Settings.CloseToTray && !_exiting && !_shutDown && !Program.IsDebugRun
            && e.CloseReason is WindowCloseReason.WindowClosing or WindowCloseReason.Undefined)
        {
            e.Cancel = true;
            SavePlacement();
            _vm.SaveSettings();
            Hide();
            return;
        }

        // Files edited in the Files view: save them, drop the edits or stay. (Windows shutting down doesn't wait.)
        if (!_editsSettled && e.CloseReason != WindowCloseReason.OSShutdown && _vm.EditedFiles.Any())
        {
            e.Cancel = true;
            _ = AskAboutEditsThenCloseAsync();
            return;
        }

        SavePlacement();
        _vm.SaveSettings();

        // Remote Control servers need a moment to unregister their folders (Ctrl+C); killed, claude.ai
        // keeps the folders "served" and the next start has to wait. So close once they stopped.
        if (_vm.HasPhoneServers && !_shutDown)
        {
            e.Cancel = true;
            Hide();
            _ = ShutDownThenCloseAsync();
            return;
        }

        _ = _vm.ShutdownAsync();

        // A downloaded update is applied by Velopack's updater once Nomad has exited.
        _vm.Updates.OnExit();
    }

    private bool _editsSettled;

    private async Task AskAboutEditsThenCloseAsync()
    {
        var exiting = _exiting;
        if (!IsVisible || WindowState == WindowState.Minimized)
        {
            ShowFromTray(); // Exit from the tray with the window hidden: the question needs it.
        }

        if (await _vm.Unsaved.AskAsync(_vm.EditedFiles, "quit"))
        {
            _editsSettled = true;
            _exiting = exiting;
            Close();
        }
        else
        {
            _exiting = false;
        }
    }

    private void SavePlacement()
    {
        var settings = _vm.Settings;
        if (_vm.Dev.Size is null && _vm.Dev.ScreenshotPath is null)
        {
            // A --dev-size or --dev-screenshot run is a test: it leaves the remembered window placement alone.
            settings.WindowMaximized = WindowState == WindowState.Maximized;
            if (WindowState == WindowState.Normal)
            {
                settings.WindowWidth = Width;
                settings.WindowHeight = Height;
            }
        }
    }

    private bool _shutDown;

    private async Task ShutDownThenCloseAsync()
    {
        try
        {
            await _vm.ShutdownAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }
        catch (TimeoutException)
        {
        }

        _shutDown = true;
        Close();
    }

    private async Task StartAsync()
    {
        var dev = _vm.Dev;
        if (dev.Answer is { } answer)
        {
            var choice = answer switch
            {
                "session" => PermissionChoice.AllowForSession,
                "deny" => PermissionChoice.Deny,
                _ => PermissionChoice.Allow,
            };
            _vm.PermissionRequested += session => DispatcherTimer.RunOnce(
                () =>
                {
                    if (answer == "pick")
                    {
                        // Choose a question's first options but leave the card open (to screenshot it).
                        (session.PendingPermission as QuestionItem)?.PickDefaults();
                    }
                    else
                    {
                        session.AnswerPending(choice, pickDefaults: true);
                    }
                },
                TimeSpan.FromSeconds(1.5));
        }

        await _vm.RefreshSessionsAsync();
        _ = _vm.ResumeInterruptedAsync();

        _vm.Updates.RequestExit = Exit;
        _vm.Updates.Start();

        if ((dev.SessionId ?? Program.LaunchSessionId) is { } sessionId)
        {
            await _vm.OpenSessionByIdAsync(sessionId);
        }

        if (dev.Prompt is { } prompt)
        {
            var session = _vm.Current;
            _ = session.SendPromptAsync(prompt);
            if (dev.StopAfterSeconds is { } stopAfter)
            {
                DispatcherTimer.RunOnce(() => session.StopCommand.Execute(null), TimeSpan.FromSeconds(stopAfter));
            }
        }

        if (dev.CloseAfter is { } closeAt)
        {
            DispatcherTimer.RunOnce(Close, TimeSpan.FromSeconds(closeAt));
        }

        if (dev.Type is var (typed, typeAt))
        {
            DispatcherTimer.RunOnce(() => _ = TypeAsync(typed), TimeSpan.FromSeconds(typeAt));
        }

        if (dev.Key is var (gesture, at))
        {
            DispatcherTimer.RunOnce(() => PressKey(KeyGesture.Parse(gesture)), TimeSpan.FromSeconds(at));
        }

        if (dev.Search is { } search)
        {
            DispatcherTimer.RunOnce(() => _vm.Search.Open(search), TimeSpan.FromSeconds(1.5));
        }

        if (dev.Find is { } find)
        {
            DispatcherTimer.RunOnce(() =>
            {
                if (this.GetVisualDescendants().OfType<SessionView>().FirstOrDefault() is { } view && view.OpenFind())
                {
                    view.FindText = find;
                }
            }, TimeSpan.FromSeconds(2));
        }

        if (dev.Files)
        {
            DispatcherTimer.RunOnce(() =>
            {
                var session = _vm.Current;
                session.IsFilesView = session.HasFolder;
                if (dev.FilesOpen is { } open && session.Folder is { } folder)
                {
                    _ = OpenAsync();

                    async Task OpenAsync()
                    {
                        foreach (var file in open.Split('|', StringSplitOptions.RemoveEmptyEntries))
                        {
                            await session.Files.RevealAsync(Path.GetFullPath(Path.Combine(folder, file)));
                        }

                        if (dev.FilesSplit && session.Files.ActiveGroup is { ActiveTab: { } last } group)
                        {
                            session.Files.OpenToSide(group, last);
                        }
                    }
                }
            }, TimeSpan.FromSeconds(1.5));
        }

        if (dev.Compose is { } compose)
        {
            DispatcherTimer.RunOnce(() => _vm.Current.ComposerText = compose, TimeSpan.FromSeconds(1.5));
        }

        foreach (var queued in dev.Queue)
        {
            _vm.Current.Queue(queued, []);
        }

        if (dev.PasteImages.Count > 0)
        {
            _vm.Current.AddAttachments(dev.PasteImages.Select(Infrastructure.PromptImages.FromPath));
        }

        if (dev.Drag is var (x1, y1, x2, y2, dragAt))
        {
            DispatcherTimer.RunOnce(() => _ = DragAsync(new Point(x1, y1), new Point(x2, y2)), TimeSpan.FromSeconds(dragAt));
        }

        if (dev.ScreenshotPath is { } path)
        {
            await CaptureAndExitAsync(path);
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetKeyboardState(byte[] state);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetKeyboardState(byte[] state);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr SendMessageW(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    /// <summary>
    /// <c>--dev-drag</c>: a left-button drag sent as mouse messages to this window only (the real cursor never moves),
    /// so Avalonia routes and captures it like a real one.
    /// </summary>
    private async Task DragAsync(Point from, Point to)
    {
        if (TryGetPlatformHandle()?.Handle is not { } hwnd)
        {
            return;
        }

        const uint mouseMove = 0x0200, buttonDown = 0x0201, buttonUp = 0x0202;
        var leftButton = 0x0001;
        var ctrl = _vm.Dev.DragCtrl ? 0x0008 : 0; // MK_CONTROL
        var scale = RenderScaling;
        void Send(uint message, Point at, int keys)
        {
            var x = (int)Math.Round(at.X * scale);
            var y = (int)Math.Round(at.Y * scale);
            SendMessageW(hwnd, message, keys, (y << 16) | (x & 0xFFFF));
        }

        // Avalonia reads modifiers from the thread's keyboard state: hold Ctrl there (this thread only, never the user's
        // keyboard) for the drag.
        var keys = new byte[256];
        if (ctrl != 0 && GetKeyboardState(keys))
        {
            var held = (byte[])keys.Clone();
            held[0x11] = held[0xA2] = 0x80; // VK_CONTROL, VK_LCONTROL
            SetKeyboardState(held);
        }

        Send(mouseMove, from, ctrl);
        Send(buttonDown, from, leftButton | ctrl);
        for (var i = 1; i <= 10; i++)
        {
            await Task.Delay(16);
            Send(mouseMove, from + ((to - from) * (i / 10.0)), leftButton | ctrl);
        }

        Send(buttonUp, to, ctrl);
        Send(mouseMove, to, ctrl);
        if (ctrl != 0)
        {
            SetKeyboardState(keys);
        } // the pointer stays there: hover styles show as after a real drag
        var focused = FocusManager?.GetFocusedElement();
        Console.Error.WriteLine($"--dev-drag {from} -> {to}: focused {focused?.GetType().Name}, selected \"{(focused as SelectableTextBlock)?.SelectedText}\"");
    }

    /// <summary><c>--dev-type</c>: characters as text input, and keys in braces pressed, a little apart.</summary>
    private async Task TypeAsync(string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            var close = text[i] == '{' ? text.IndexOf('}', i) : -1;
            if (close > i + 1)
            {
                PressKey(KeyGesture.Parse(text[(i + 1)..close]));
                i = close;
            }
            else
            {
                var target = FocusManager?.GetFocusedElement() as Interactive ?? this;
                target.RaiseEvent(new TextInputEventArgs { RoutedEvent = TextInputEvent, Text = text[i].ToString(), Source = target });
            }

            await Task.Delay(30);
        }
    }

    /// <summary><c>--dev-key</c>: a key press routed like a real one, from the focused control up.</summary>
    private void PressKey(KeyGesture gesture)
    {
        var target = FocusManager?.GetFocusedElement() as Interactive ?? this;
        target.RaiseEvent(new KeyEventArgs { RoutedEvent = KeyDownEvent, Key = gesture.Key, KeyModifiers = gesture.KeyModifiers, Source = target });
        Console.Error.WriteLine($"--dev-key {gesture} on {target.GetType().Name}");
    }

    /// <summary><c>--dev-screenshot</c>: waits, opens what was asked for, renders the window to a PNG and exits.</summary>
    private async Task CaptureAndExitAsync(string path)
    {
        var dev = _vm.Dev;
        var deadline = Task.Delay(TimeSpan.FromSeconds(dev.AfterSeconds));
        if (dev.WaitForTurn && dev.Prompt is not null)
        {
            var finished = new TaskCompletionSource();
            _vm.TurnCompleted += _ => finished.TrySetResult();
            await Task.WhenAny(finished.Task, deadline);
            await Task.Delay(700);
        }
        else
        {
            await deadline;
        }

        if (dev.ExpandTools)
        {
            _vm.Current.ExpandAllTools();
        }

        if (dev.Set.Count > 0)
        {
            // Same path as the settings dialog: live re-render of the open transcript.
            var page = new SettingsViewModel(_vm);
            foreach (var (key, value) in dev.Set)
            {
                switch (key)
                {
                    case "textSize": page.TextSize = value; break;
                    case "width": page.TranscriptWidth = value; break;
                    case "theme": page.Theme = value; break;
                    case "colorTheme": page.SelectedColorTheme = page.ColorThemes.FirstOrDefault(c => c.Id == value) ?? page.SelectedColorTheme; break;
                }
            }

            await Task.Delay(500);
        }

        if (dev.SettingsOpen)
        {
            _vm.OpenSettingsCommand.Execute(null);
            if (dev.SettingsHubPage)
            {
                _vm.SettingsPage?.ShowHubCommand.Execute(null);
            }
            else if (dev.SettingsClaudePage)
            {
                _vm.SettingsPage?.ShowClaudeCodeCommand.Execute(null);
            }
            else if (dev.SettingsUpdatesPage)
            {
                _vm.SettingsPage?.ShowUpdatesCommand.Execute(null);

                // The release list comes from GitHub: wait for it (at most 20 s).
                await Task.WhenAny(_vm.Updates.GetReleasesAsync(), Task.Delay(TimeSpan.FromSeconds(20)));
            }
        }

        foreach (var row in _vm.Recents.OfType<SessionListItemViewModel>().Take(dev.Pick))
        {
            _vm.TogglePick(row);
        }

        if (dev.Menu == "update")
        {
            FlyoutBase.ShowAttachedFlyout(UpdateChip);
        }
        else if (dev.Menu == "list")
        {
            ListOptionsButton.Flyout?.ShowAt(ListOptionsButton);
        }
        else if (dev.Menu == "alivelist")
        {
            AliveOptionsButton.Flyout?.ShowAt(AliveOptionsButton);
        }
        else if (dev.Menu == "group")
        {
            // The first folder heading's context menu (Pin to Alive / Unpin from Alive).
            if (this.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.DataContext is SessionGroupHeader { CanPin: true }) is { } groupButton)
            {
                groupButton.RaiseEvent(new ContextRequestedEventArgs { RoutedEvent = ContextRequestedEvent, Source = groupButton });
                if (groupButton.ContextMenu is { IsOpen: false } groupMenu)
                {
                    groupMenu.Open();
                }
            }
        }
        else if (dev.Menu == "row")
        {
            // The first Idle row's context menu, asked for as a right-click asks (so the window names its entries).
            if (this.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.DataContext is SessionListItemViewModel { IsAlive: false }) is { } rowButton)
            {
                rowButton.RaiseEvent(new ContextRequestedEventArgs { RoutedEvent = ContextRequestedEvent, Source = rowButton });
                if (rowButton.ContextMenu is { IsOpen: false } rowMenu)
                {
                    rowMenu.Open();
                }
            }
        }
        else if (dev.Menu is { } menu)
        {
            this.GetVisualDescendants().OfType<SessionView>().FirstOrDefault()?.OpenMenu(menu);
        }

        await Task.Delay(600);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

        try
        {
            SaveScreenshot(path);
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync("Screenshot failed: " + ex.Message);
        }

        _editsSettled = true; // A test run doesn't wait on a question.
        Close();
    }

    private void SaveScreenshot(string path)
    {
        var scaling = RenderScaling;
        var pixelSize = new PixelSize(Math.Max(1, (int)(Bounds.Width * scaling)), Math.Max(1, (int)(Bounds.Height * scaling)));
        using var bitmap = new RenderTargetBitmap(pixelSize, new Vector(96 * scaling, 96 * scaling));
        bitmap.Render(this);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        bitmap.Save(path);
    }
}
