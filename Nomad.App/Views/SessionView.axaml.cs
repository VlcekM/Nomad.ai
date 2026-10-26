using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
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

        // The effort thumb glides while dragged and settles on the nearest stop when let go.
        EffortSlider.AddHandler(Thumb.DragCompletedEvent, (_, _) => Vm?.SnapEffort());
        EffortSlider.AddHandler(PointerReleasedEvent, (_, _) => Vm?.SnapEffort(), RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
    }

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
        if (_vm is not null)
        {
            _vm.ContentChanged += OnContentChanged;
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
        }
    }

    private void HideMenus()
    {
        ModelChip.Flyout?.Hide();
        ModeChip.Flyout?.Hide();
        PlusButton.Flyout?.Hide();
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

    private void OnSettingsClick(object? sender, RoutedEventArgs e)
    {
        HideMenus();
        if (TopLevel.GetTopLevel(this)?.DataContext is MainViewModel main)
        {
            main.OpenSettingsCommand.Execute(null);
        }
    }
}
