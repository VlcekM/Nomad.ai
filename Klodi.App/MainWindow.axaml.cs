using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Klodi.App.ViewModels;
using Klodi.App.Views;
using Klodi.Core.Agents;

namespace Klodi.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;

    public MainWindow(MainViewModel vm)
    {
        _vm = vm;
        DataContext = vm;
        InitializeComponent();

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

        Opened += (_, _) => _ = StartAsync();
        Closing += OnClosing;
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

    private void OnExitClick(object? sender, RoutedEventArgs e) => Close();

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

    private void OnMenuItemClick(object? sender, RoutedEventArgs e) => AppMenuButton.Flyout?.Hide();

    private void OnUpdateLaterClick(object? sender, RoutedEventArgs e) => UpdateChipButton.Flyout?.Hide();

    // ------------------------------------------------------------------ lifetime

    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        SavePlacement();
        _vm.SaveSettings();
        _ = _vm.ShutdownAsync();

        // A downloaded update is applied by Velopack's updater once Klodi has exited.
        _vm.Updates.OnExit();
    }

    /// <summary>Restarting into an update ends the process at once: save what closing the window would save.</summary>
    private async Task PrepareRestartAsync()
    {
        SavePlacement();
        _vm.SaveSettings();
        await _vm.ShutdownAsync();
    }

    private void SavePlacement()
    {
        var settings = _vm.Settings;
        if (_vm.Dev.Size is null)
        {
            // A --dev-size run is a test: it leaves the remembered window placement alone.
            settings.WindowMaximized = WindowState == WindowState.Maximized;
            if (WindowState == WindowState.Normal)
            {
                settings.WindowWidth = Width;
                settings.WindowHeight = Height;
            }
        }
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

        _vm.Updates.BeforeRestart = PrepareRestartAsync;
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

        if (dev.ScreenshotPath is { } path)
        {
            await CaptureAndExitAsync(path);
        }
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

        if (dev.Menu is { } menu)
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
