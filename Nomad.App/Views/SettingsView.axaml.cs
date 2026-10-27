using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
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

    private async void OnAddPhoneFolderClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsViewModel vm || TopLevel.GetTopLevel(this) is not { } top)
        {
            return;
        }

        var result = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Folder the phone can start sessions in",
            AllowMultiple = false,
        });

        if (result.Count > 0 && result[0].TryGetLocalPath() is { } path)
        {
            vm.AddPhoneFolder(path);
        }
    }

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
