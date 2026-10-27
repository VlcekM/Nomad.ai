using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
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

    private readonly DispatcherTimer _hintTimer;

    private void HideShortcutHints()
    {
        _hintTimer.Stop();
        _vm.ShowShortcutHints(false);
    }

    public MainWindow(MainViewModel vm)
    {
        _vm = vm;
        DataContext = vm;
        InitializeComponent();

        // A session done or waiting while the window is in the background: a notification and/or a flashing taskbar button.
        vm.TurnCompleted += session =>
        {
            if (session.Queued.Count == 0)
            {
                var reply = session.Items.OfType<AssistantTextItem>().LastOrDefault()?.Text;
                CallForAttention(session.Title, string.IsNullOrWhiteSpace(reply) ? "Finished" : Nomad.Core.Sessions.SessionTitles.Shorten(reply, 140));
            }
        };
        vm.PermissionRequested += session => CallForAttention(
            session.Title,
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
            // The rename box sits in the row's button, which would take Enter and Space as a click: Enter saves, Esc keeps
            // the old name, and Space stops here (the space itself still arrives as text input).
            if (e.Source is TextBox { DataContext: SessionListItemViewModel { IsRenaming: true } renaming } && e.Key is Key.Enter or Key.Escape or Key.Space)
            {
                if (e.Key != Key.Space)
                {
                    vm.EndRename(renaming, save: e.Key == Key.Enter);
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

                e.Handled = true;
                return;
            }

            if (e.Key == Key.F2 && e.KeyModifiers == KeyModifiers.None && vm.CurrentRow is { } row)
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

            // Ctrl+1…9 opens the sidebar's rows top to bottom; Ctrl held a moment shows their numbers.
            if (e.KeyModifiers == KeyModifiers.Control && e.Key is >= Key.D1 and <= Key.D9 or >= Key.NumPad1 and <= Key.NumPad9)
            {
                var number = e.Key >= Key.NumPad1 ? e.Key - Key.NumPad1 + 1 : e.Key - Key.D1 + 1;
                e.Handled = vm.OpenByShortcut(number);
            }
            else if (e.Key is Key.LeftCtrl or Key.RightCtrl)
            {
                _hintTimer.Stop();
                _hintTimer.Start();
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
        _hintTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(400), DispatcherPriority.Input, (_, _) =>
        {
            _hintTimer!.Stop();
            vm.ShowShortcutHints(true);
        });

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
        CreateTrayIcon();
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.IsSettingsOpen) && vm.IsSettingsOpen)
            {
                Dispatcher.UIThread.Post(() => SettingsHost.Focus(), DispatcherPriority.Loaded);
            }
        };
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

    private void OnRecentClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: SessionListItemViewModel item })
        {
            _ = _vm.OpenSessionAsync(item);
        }
    }

    private void OnPanelStripPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            _vm.ToggleSidePanel();
            e.Handled = true;
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
        if (sender is Control { DataContext: SessionListItemViewModel { IsRenaming: false } item })
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
            _ = _vm.ToggleAliveAsync(item);
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
            _ = _vm.ToggleArchivedAsync(item);
        }
    }

    private void OnDeleteSessionClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: SessionListItemViewModel item })
        {
            _ = _vm.DeleteSessionAsync(item);
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

    private void OnGroupHeaderClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: SessionGroupHeader header })
        {
            _ = _vm.ToggleGroupAsync(header);
        }
    }

    private void OnMenuItemClick(object? sender, RoutedEventArgs e) => AppMenuButton.Flyout?.Hide();

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

    /// <summary>With "close to tray" on, an icon in the notification area: a click shows the window, its menu exits.</summary>
    private void CreateTrayIcon()
    {
        if (_vm.Dev.ScreenshotPath is not null || Application.Current is not { } app)
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

    /// <summary>Nomad.ai was launched again: this window shows, restored and in front.</summary>
    public void BringToFront() => ShowFromTray();

    /// <summary>Only while Nomad.ai is not the window in front (minimized, in the tray or behind another app).</summary>
    private void CallForAttention(string? title, string text)
    {
        if (IsActive && IsVisible && WindowState != WindowState.Minimized)
        {
            return;
        }

        if (_vm.Settings.NotifyWhenDone)
        {
            Infrastructure.WindowsToast.Show(string.IsNullOrWhiteSpace(title) ? "Nomad.ai" : title, text);
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
        if (_vm.Settings.CloseToTray && !_exiting && !_shutDown && _vm.Dev.ScreenshotPath is null
            && e.CloseReason is WindowCloseReason.WindowClosing or WindowCloseReason.Undefined)
        {
            e.Cancel = true;
            SavePlacement();
            _vm.SaveSettings();
            Hide();
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

        _vm.Updates.RequestExit = Exit;
        _vm.Updates.Start();

        if (dev.SessionId is { } sessionId)
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

        if (dev.Key is var (gesture, at))
        {
            DispatcherTimer.RunOnce(() => PressKey(KeyGesture.Parse(gesture)), TimeSpan.FromSeconds(at));
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
        const int leftButton = 0x0001;
        var scale = RenderScaling;
        void Send(uint message, Point at, int keys)
        {
            var x = (int)Math.Round(at.X * scale);
            var y = (int)Math.Round(at.Y * scale);
            SendMessageW(hwnd, message, keys, (y << 16) | (x & 0xFFFF));
        }

        Send(mouseMove, from, 0);
        Send(buttonDown, from, leftButton);
        for (var i = 1; i <= 10; i++)
        {
            await Task.Delay(16);
            Send(mouseMove, from + ((to - from) * (i / 10.0)), leftButton);
        }

        Send(buttonUp, to, 0);
        Send(mouseMove, to, 0); // the pointer stays there: hover styles show as after a real drag
        var focused = FocusManager?.GetFocusedElement();
        Console.Error.WriteLine($"--dev-drag {from} -> {to}: focused {focused?.GetType().Name}, selected \"{(focused as SelectableTextBlock)?.SelectedText}\"");
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
                }
            }

            await Task.Delay(500);
        }

        if (dev.SettingsOpen)
        {
            _vm.OpenSettingsCommand.Execute(null);
            if (dev.SettingsClaudePage)
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

        if (dev.Menu == "update")
        {
            FlyoutBase.ShowAttachedFlyout(UpdateChip);
        }
        else if (dev.Menu == "list")
        {
            ListOptionsButton.Flyout?.ShowAt(ListOptionsButton);
        }
        else if (dev.Menu == "row")
        {
            // The first Idle row's context menu.
            this.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.DataContext is SessionListItemViewModel { IsAlive: false })
                ?.ContextMenu?.Open();
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
