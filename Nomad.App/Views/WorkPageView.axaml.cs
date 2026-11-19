using Avalonia.Controls;
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
    }

    /// <summary>
    /// A repository's card or a row that comes in fades in and rises into place.
    /// </summary>
    private void OnContainerPrepared(object? sender, ContainerPreparedEventArgs e) => Entrance.Play(e.Container);
}
