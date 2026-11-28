using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;

namespace Nomad.App;

public partial class MainWindow
{
    #region Linux window chrome

    /// <summary>
    /// Keeps Nomad's title bar and supplies resize grips where Linux would normally draw a window frame.
    /// </summary>
    private void ConfigureLinuxWindowChrome()
    {
        ExtendClientAreaToDecorationsHint = false;
        WindowDecorations = WindowDecorations.None;

        var grips = new Grid { RowDefinitions = new("7,*,7"), ColumnDefinitions = new("7,*,7") };
        AddResizeGrip(grips, 0, 0, WindowEdge.NorthWest, StandardCursorType.TopLeftCorner);
        AddResizeGrip(grips, 0, 1, WindowEdge.North, StandardCursorType.SizeNorthSouth);
        AddResizeGrip(grips, 0, 2, WindowEdge.NorthEast, StandardCursorType.TopRightCorner);
        AddResizeGrip(grips, 1, 0, WindowEdge.West, StandardCursorType.SizeWestEast);
        AddResizeGrip(grips, 1, 2, WindowEdge.East, StandardCursorType.SizeWestEast);
        AddResizeGrip(grips, 2, 0, WindowEdge.SouthWest, StandardCursorType.BottomLeftCorner);
        AddResizeGrip(grips, 2, 1, WindowEdge.South, StandardCursorType.SizeNorthSouth);
        AddResizeGrip(grips, 2, 2, WindowEdge.SouthEast, StandardCursorType.BottomRightCorner);

        WindowContent.Children.Add(grips);
        grips.IsVisible = CanResize && WindowState == WindowState.Normal;
        PropertyChanged += (_, e) =>
        {
            if (e.Property == WindowStateProperty || e.Property == CanResizeProperty)
                grips.IsVisible = CanResize && WindowState == WindowState.Normal;
        };
    }

    /// <summary>
    /// Adds a transparent pointer target that hands resizing off to the window manager.
    /// </summary>
    private void AddResizeGrip(Grid grips, int row, int column, WindowEdge edge, StandardCursorType cursor)
    {
        var grip = new Border { Background = Brushes.Transparent, Cursor = new Cursor(cursor) };
        Grid.SetRow(grip, row);
        Grid.SetColumn(grip, column);
        grip.AddHandler(PointerPressedEvent, (_, e) =>
        {
            if (e.GetCurrentPoint(grip).Properties.PointerUpdateKind != PointerUpdateKind.LeftButtonPressed) return;

            BeginResizeDrag(edge, e);
            e.Handled = true;
        }, RoutingStrategies.Tunnel);

        grips.Children.Add(grip);
    }

    #endregion
}
