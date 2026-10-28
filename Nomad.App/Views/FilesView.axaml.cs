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
    }

    private MainViewModel? Main => TopLevel.GetTopLevel(this)?.DataContext as MainViewModel;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_session is not null)
        {
            _session.PropertyChanged -= OnSessionChanged;
            _session.Files.PropertyChanged -= OnFilesChanged;
            _session.Files.Show(false);
        }

        _session = DataContext as SessionViewModel;
        if (_session is not null)
        {
            _session.PropertyChanged += OnSessionChanged;
            _session.Files.PropertyChanged += OnFilesChanged;
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

    // A file picked in the tree: its text fades in.
    private void OnFilesChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(FilesViewModel.PreviewText))
        {
            FileScroller.Offset = default;
            Entrance.Fade(FilePane);
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
        TreePane.BorderThickness = right ? new Thickness(1, 0, 0, 0) : new Thickness(0, 0, 1, 0);
        TreeGrip.HorizontalAlignment = right ? HorizontalAlignment.Left : HorizontalAlignment.Right;
        TreeGrip.Margin = right ? new Thickness(-4, 0, 0, 0) : new Thickness(0, 0, -4, 0);
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
