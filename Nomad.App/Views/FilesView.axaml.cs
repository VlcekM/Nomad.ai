using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Nomad.App.Controls;
using Nomad.App.ViewModels;

namespace Nomad.App.Views;

public partial class FilesView : UserControl
{
    private SessionViewModel? _session;
    private bool _attached;
    private double? _gripOffset;

    public FilesView()
    {
        InitializeComponent();
        Tree.FileOpened += (path, keep) => _session?.Files.Open(path, keep);
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

        _session = DataContext as SessionViewModel;
        if (_session is not null)
        {
            _session.PropertyChanged += OnSessionChanged;
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
