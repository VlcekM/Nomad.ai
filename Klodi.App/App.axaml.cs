using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Klodi.App.Markdown;
using Klodi.App.ViewModels;
using Klodi.Core.Process;
using Klodi.Core.Settings;

namespace Klodi.App;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var dev = Program.Dev;
            var settingsPath = dev.SettingsPath ?? SettingsStore.DefaultPath;
            var settings = SettingsStore.Load(settingsPath);
            ApplyAppearance(settings);

            var vm = new MainViewModel(settings, settingsPath, dev);
            desktop.MainWindow = new MainWindow(vm);
            desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
            desktop.Exit += (_, _) => ClaudeCliProcess.KillAllOwned();
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>Pushes the appearance settings into app resources and the markdown style.</summary>
    public static void ApplyAppearance(KlodiSettings settings)
    {
        if (Current is not { } app)
        {
            return;
        }

        app.Resources["UiFont"] = Fonts.Ui(settings.InterfaceFont);
        app.Resources["MonoFont"] = Fonts.Code(settings.CodeFont);
        app.Resources["ColumnMaxWidth"] = ColumnWidth(settings.TranscriptWidth);

        var style = MarkdownStyle.From(settings.TextSize, settings.InterfaceFont, settings.CodeFont);
        app.Resources["BodyFontSize"] = style.BodySize;
        app.Resources["BodyLineHeight"] = style.LineHeight;
        app.Resources["CodeFontSize"] = style.CodeSize;
        MarkdownStyle.Current = style;
        MarkdownView.NotifyStyleChanged();
    }

    /// <summary>Width of the transcript/composer column (Wide matches the original's 1280 px).</summary>
    public static double ColumnWidth(string? setting) => setting switch
    {
        "Narrow" => 760,
        "Medium" => 1000,
        _ => 1280,
    };
}
