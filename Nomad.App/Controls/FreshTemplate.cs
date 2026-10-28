using Avalonia.Controls;
using Avalonia.Controls.Templates;

namespace Nomad.App.Controls;

/// <summary>
/// An item template for a virtualized list that builds each item's view afresh from the given typed templates,
/// instead of handing a recycled container the view of the item it showed before: bound state (folds, chevrons,
/// ticks) would otherwise animate from the old item's values to the new one's as rows scroll into view.
/// </summary>
public sealed class FreshTemplate(IEnumerable<IDataTemplate> templates) : IDataTemplate
{
    public Control? Build(object? param) => templates.FirstOrDefault(t => t.Match(param))?.Build(param);

    public bool Match(object? data) => templates.Any(t => t.Match(data));
}
