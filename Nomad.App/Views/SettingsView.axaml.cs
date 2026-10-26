using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Nomad.App.ViewModels;

namespace Nomad.App.Views;

public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }
    }

    private void Close()
    {
        if (TopLevel.GetTopLevel(this)?.DataContext is MainViewModel main)
        {
            main.CloseSettingsCommand.Execute(null);
        }
    }
}
