using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Nomad.App.Controls;

namespace Nomad.App.Views;

/// <summary>
/// The pull requests and issues page (see <see cref="ViewModels.WorkPageViewModel"/>).
/// </summary>
public partial class WorkPageView : UserControl
{
    /// <summary>
    /// Builds the page from its XAML.
    /// </summary>
    public WorkPageView()
    {
        InitializeComponent();
        AddHandler(PointerPressedEvent, OnPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    /// <summary>
    /// Notes whether Ctrl is held before a button's click runs: Fix, Review and Merge start in a new worktree then.
    /// </summary>
    private void OnPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is ViewModels.WorkPageViewModel page) page.CtrlClick = e.KeyModifiers.HasFlag(KeyModifiers.Control);
    }

    /// <summary>
    /// Eases the reordered list into place while keeping it interactive.
    /// </summary>
    private void OnSortChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (DataContext is ViewModels.WorkPageViewModel { HasGroups: true }) Glide.Play(WorkGroups, new Avalonia.Vector(0, 5), 180);
    }

    /// <summary>
    /// A repository's card or a row that comes in fades in and rises into place.
    /// </summary>
    private void OnContainerPrepared(object? sender, ContainerPreparedEventArgs e) => Entrance.Play(e.Container);
}
