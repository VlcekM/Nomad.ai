using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Nomad.App.Controls;
using Nomad.App.ViewModels;

namespace Nomad.App.Views;

public partial class EditorGroupView : UserControl
{
    private EditorGroup? _group;
    private FileTab? _shown;

    public EditorGroupView()
    {
        InitializeComponent();

        // Any click in the group (the text handles its own) makes it the one files from the tree open in.
        AddHandler(PointerPressedEvent, (_, _) => _group?.TakeFocus(), RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_group is not null)
        {
            _group.PropertyChanged -= OnGroupChanged;
        }

        _group = DataContext as EditorGroup;
        if (_group is not null)
        {
            _group.PropertyChanged += OnGroupChanged;
        }

        _shown = _group?.ActiveTab;
    }

    // Another tab: keep where the last one was scrolled to, bring this one back to its place, and fade it in.
    private void OnGroupChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(EditorGroup.ActiveTab) || _group is null)
        {
            return;
        }

        if (_shown is not null)
        {
            _shown.ScrollOffset = Scroller.Offset;
        }

        _shown = _group.ActiveTab;
        Scroller.Offset = _shown?.ScrollOffset ?? default;
        if (_shown is not null)
        {
            Entrance.Fade(Document);
        }
    }

    // Left click shows the tab (a double-click keeps a preview); the middle button closes it.
    private void OnTabPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control { DataContext: FileTab tab } || _group is null)
        {
            return;
        }

        var point = e.GetCurrentPoint(this).Properties;
        if (point.IsMiddleButtonPressed)
        {
            _group.CloseCommand.Execute(tab);
            e.Handled = true;
        }
        else if (point.IsLeftButtonPressed)
        {
            _group.ActivateCommand.Execute(tab);
            if (e.ClickCount >= 2)
            {
                _group.KeepCommand.Execute(tab);
            }

            e.Handled = true;
        }
    }

    // A new tab fades in.
    private void OnTabLoaded(object? sender, RoutedEventArgs e)
    {
        if (sender is Control tab)
        {
            Entrance.Fade(tab);
        }
    }
}
