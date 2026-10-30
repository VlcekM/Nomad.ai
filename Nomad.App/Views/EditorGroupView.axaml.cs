using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
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
        Code.DefinitionRequested += (name, receiver, line) => _group?.GoToDefinition(name, receiver, line);
        Code.Resolve = (name, receiver, line) => _group?.Resolve(name, receiver, line) ?? [];
        Code.Describe = symbol => _group?.Describe(symbol);
        Code.AskRequested += (from, to, text) => _group?.AskAbout(from, to, text);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_group is not null)
        {
            _group.PropertyChanged -= OnGroupChanged;
            _group.Find.FocusRequested -= OnFindFocusRequested;
        }

        _group = DataContext as EditorGroup;
        if (_group is not null)
        {
            _group.PropertyChanged += OnGroupChanged;
            _group.Find.FocusRequested += OnFindFocusRequested;
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

    // Ctrl+F puts the focus in the query (Ctrl+H in the replacement, when there is a query already).
    private void OnFindFocusRequested(bool replace)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var box = replace && FindQuery.Text is { Length: > 0 } ? FindReplace : FindQuery;
            box.Focus();
            box.SelectAll();
        }, DispatcherPriority.Loaded);
    }

    // In the find box: Enter goes to the next match (Shift: the previous), Esc closes it and goes back to the code.
    private void OnFindKeyDown(object? sender, KeyEventArgs e)
    {
        if (_group is null)
        {
            return;
        }

        if (e.Key == Key.Enter)
        {
            if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
            {
                _group.Find.Previous();
            }
            else
            {
                _group.Find.Next();
            }

            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            CloseFind();
            e.Handled = true;
        }
    }

    // In the replacement: Enter replaces the next match, Ctrl+Alt+Enter all of them, Esc closes.
    private void OnReplaceKeyDown(object? sender, KeyEventArgs e)
    {
        if (_group is null)
        {
            return;
        }

        if (e.Key == Key.Enter)
        {
            (e.KeyModifiers.HasFlag(KeyModifiers.Alt) ? _group.Find.ReplaceAllCommand : _group.Find.ReplaceCommand).Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            CloseFind();
            e.Handled = true;
        }
    }

    private void OnFindClose(object? sender, RoutedEventArgs e) => CloseFind();

    private void CloseFind()
    {
        _group?.Find.Close();
        Code.Focus();
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
