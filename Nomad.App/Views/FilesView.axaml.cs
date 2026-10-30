using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Threading;
using Nomad.App.Controls;
using Nomad.App.ViewModels;

namespace Nomad.App.Views;

public partial class FilesView : UserControl
{
    private SessionViewModel? _session;
    private bool _attached;
    private double? _gripOffset;
    private CodeSearchViewModel? _codeSearch;

    public FilesView()
    {
        InitializeComponent();
        Tree.FileOpened += (path, keep) => _session?.Files.Open(path, keep);
        Tree.DiffRequested += path => _session?.Files.OpenDiff(path);
    }

    private MainViewModel? Main => TopLevel.GetTopLevel(this)?.DataContext as MainViewModel;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_session is not null)
        {
            _session.PropertyChanged -= OnSessionChanged;
            _session.Files.Show(false);
        }

        _codeSearch?.Close();
        if (_codeSearch is not null)
        {
            _codeSearch.PropertyChanged -= OnCodeSearchChanged;
        }

        _session = DataContext as SessionViewModel;
        _codeSearch = _session?.Files.CodeSearch;
        if (_session is not null)
        {
            _session.PropertyChanged += OnSessionChanged;
        }

        if (_codeSearch is not null)
        {
            _codeSearch.PropertyChanged += OnCodeSearchChanged;
        }

        ApplySide();
        ApplyShown();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attached = true;
        ApplyShown();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _attached = false;
        ApplyShown();
    }

    private void OnSessionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SessionViewModel.IsFilesView))
        {
            ApplyShown();
            if (_session is { IsFilesView: false })
            {
                _session.Files.CodeSearch.Close();
            }

            // Back to the chat: typing goes to its composer again, not to the tree fading out.
            if (_session is { IsFilesView: false } && (Parent as Panel)?.Children.OfType<SessionView>().FirstOrDefault() is { } chat)
            {
                chat.FocusComposerSoon();
            }
        }
        else if (e.PropertyName == nameof(SessionViewModel.FileTreeOnRight))
        {
            ApplySide();
        }
    }

    // The mouse's back and forward buttons go back and forth between the places jumped to.
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var props = e.GetCurrentPoint(this).Properties;
        if (props.IsXButton1Pressed)
        {
            _session?.Files.NavigateBack();
            e.Handled = true;
        }
        else if (props.IsXButton2Pressed)
        {
            _session?.Files.NavigateForward();
            e.Handled = true;
        }
    }

    // Ctrl+W closes the tab shown in the focused group; Ctrl+S saves it.
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.S && e.KeyModifiers == KeyModifiers.Control && _session?.Files.ActiveGroup is { ActiveTab: { } edited })
        {
            edited.SaveCommand.Execute(null);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.W && e.KeyModifiers == KeyModifiers.Control && _session?.Files.ActiveGroup is { ActiveTab: { } tab } group)
        {
            group.CloseCommand.Execute(tab);
            e.Handled = true;
        }
    }

    // The code search opening takes the focus to its box; closing gives it back to the file (or the tree).
    private void OnCodeSearchChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_codeSearch is not { } search)
        {
            return;
        }

        if (e.PropertyName == nameof(CodeSearchViewModel.IsOpen))
        {
            if (search.IsOpen)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    CodeSearchBox.Focus();
                    if (search.Query.StartsWith(':'))
                    {
                        // Ctrl+G: the line number goes after the ":".
                        CodeSearchBox.ClearSelection();
                        CodeSearchBox.CaretIndex = search.Query.Length;
                    }
                    else
                    {
                        CodeSearchBox.SelectAll();
                    }
                }, DispatcherPriority.Loaded);
            }
            else if (CodeSearchBox.IsFocused)
            {
                Tree.Focus();
            }
        }
        else if (e.PropertyName == nameof(CodeSearchViewModel.SelectedIndex) && search.SelectedIndex is >= 0 and var index)
        {
            // The arrows keep the selected row in sight (the first one scrolls back to the top).
            Dispatcher.UIThread.Post(() =>
            {
                if (index == 0)
                {
                    CodeSearchScroll.Offset = default;
                }
                else
                {
                    CodeSearchList.ScrollIntoView(index);
                }
            }, DispatcherPriority.Loaded);
        }
    }

    private void OnCodeSearchOutsidePressed(object? sender, PointerPressedEventArgs e)
    {
        _codeSearch?.Close();
        e.Handled = true;
    }

    private void OnCodeSearchResultClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: CodeSearchResult result })
        {
            _codeSearch?.OpenResult(result);
        }
    }

    /// <summary>The tree watches the folder only while this view is on screen.</summary>
    private void ApplyShown()
    {
        if (_session is { } session)
        {
            var shown = _attached && session.IsFilesView;
            session.Files.Show(shown);
            if (shown)
            {
                Tree.Focus();
            }
        }
    }

    /// <summary>The tree on the right (its grip on its left edge) or on the left (the grip on its right).</summary>
    private void ApplySide()
    {
        var right = _session?.FileTreeOnRight ?? true;
        var column = right ? 2 : 0;
        Grid.SetColumn(TreePane, column);
        Grid.SetColumn(TreeGrip, column);
        TreePane.Margin = right ? new Thickness(0, 8, 8, 12) : new Thickness(8, 8, 0, 12);
        Documents.Margin = right ? new Thickness(8, 6, 4, 12) : new Thickness(4, 6, 8, 12);
        TreeGrip.HorizontalAlignment = right ? HorizontalAlignment.Left : HorizontalAlignment.Right;
        TreeGrip.Margin = right ? new Thickness(-4, 8, 0, 12) : new Thickness(0, 8, -4, 12);
        TreeGripLine.HorizontalAlignment = right ? HorizontalAlignment.Left : HorizontalAlignment.Right;
        TreeGripLine.Margin = right ? new Thickness(3, 0, 0, 0) : new Thickness(0, 0, 3, 0);
    }

    private bool TreeOnRight => _session?.FileTreeOnRight ?? true;

    private void OnGripPressed(object? sender, PointerPressedEventArgs e)
    {
        if (Main is not { } main || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        e.Handled = true;
        if (e.ClickCount == 2)
        {
            main.FileTreeWidth = MainViewModel.DefaultFileTreeWidth;
            main.SaveSettings();
            return;
        }

        var x = e.GetPosition(this).X;
        _gripOffset = TreeOnRight ? main.FileTreeWidth + x : main.FileTreeWidth - x;
        TreeGrip.Classes.Add("dragging");
        e.Pointer.Capture(TreeGrip);
    }

    private void OnGripMoved(object? sender, PointerEventArgs e)
    {
        if (_gripOffset is { } offset && Main is { } main)
        {
            var x = e.GetPosition(this).X;
            main.FileTreeWidth = TreeOnRight ? offset - x : offset + x;
        }
    }

    private void OnGripReleased(object? sender, PointerReleasedEventArgs e) => EndGripDrag(e.Pointer);

    private void OnGripCaptureLost(object? sender, PointerCaptureLostEventArgs e) => EndGripDrag(null);

    private void EndGripDrag(IPointer? pointer)
    {
        if (_gripOffset is null)
        {
            return;
        }

        _gripOffset = null;
        pointer?.Capture(null);
        TreeGrip.Classes.Remove("dragging");
        Main?.SaveSettings();
    }
}
