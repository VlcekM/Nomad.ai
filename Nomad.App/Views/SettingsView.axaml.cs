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

    // A theme color's swatch: opens its picker.
    private void OnSwatchPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Control swatch && e.GetCurrentPoint(swatch).Properties.IsLeftButtonPressed)
        {
            e.Handled = true;
            Avalonia.Controls.Primitives.FlyoutBase.ShowAttachedFlyout(swatch);
        }
    }

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

    private static readonly FilePickerFileType ThemeFiles = new("Nomad.ai theme") { Patterns = ["*.json"] };

    private async void OnImportThemeClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsViewModel vm || TopLevel.GetTopLevel(this) is not { } top)
        {
            return;
        }

        var result = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import a theme",
            AllowMultiple = false,
            FileTypeFilter = [ThemeFiles, FilePickerFileTypes.All],
        });
        if (result.Count == 0)
        {
            return;
        }

        try
        {
            await using var stream = await result[0].OpenReadAsync();
            using var reader = new StreamReader(stream);
            vm.ImportTheme(await reader.ReadToEndAsync(), result[0].Name);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            vm.ThemeFileProblem = $"Couldn't read {result[0].Name}: {ex.Message}";
        }
    }

    private async void OnExportThemeClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsViewModel vm || TopLevel.GetTopLevel(this) is not { } top)
        {
            return;
        }

        var (fileName, json) = vm.ExportSelectedTheme();
        var file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export the theme",
            SuggestedFileName = fileName,
            DefaultExtension = "json",
            FileTypeChoices = [ThemeFiles],
            ShowOverwritePrompt = true,
        });
        if (file is null)
        {
            return;
        }

        try
        {
            await using var stream = await file.OpenWriteAsync();
            if (stream.CanSeek)
            {
                stream.SetLength(0);
            }

            await using var writer = new StreamWriter(stream);
            await writer.WriteAsync(json);
            vm.ThemeFileProblem = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            vm.ThemeFileProblem = $"Couldn't export to {file.Name}: {ex.Message}";
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
