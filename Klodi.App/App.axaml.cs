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
            var settings = Program.Settings;
            ApplyAppearance(settings);

            var vm = new MainViewModel(settings, Program.SettingsPath, Program.Dev);
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

        Set("UiFont", Fonts.Ui(settings.InterfaceFont));
        Set("MonoFont", Fonts.Code(settings.CodeFont));
        Set("ColumnMaxWidth", ColumnWidth(settings.TranscriptWidth));

        var style = MarkdownStyle.From(settings.TextSize, settings.InterfaceFont, settings.CodeFont);
        Set("BodyFontSize", style.BodySize);
        Set("BodyLineHeight", style.LineHeight);
        Set("CodeFontSize", style.CodeSize);

        var old = MarkdownStyle.Current;
        if (old.BodySize != style.BodySize || old.CodeSize != style.CodeSize ||
            old.BodyFont.Name != style.BodyFont.Name || old.CodeFont.Name != style.CodeFont.Name)
        {
            MarkdownStyle.Current = style;
            MarkdownView.NotifyStyleChanged();
        }

        void Set(string key, object value)
        {
            var same = app.Resources.TryGetValue(key, out var current) && current switch
            {
                Avalonia.Media.FontFamily f when value is Avalonia.Media.FontFamily g => f.Name == g.Name,
                _ => Equals(current, value),
            };
            if (!same)
            {
                app.Resources[key] = value;
            }
        }
    }

    /// <summary>Width of the transcript/composer column (Wide matches the original's 1280 px).</summary>
    public static double ColumnWidth(string? setting) => setting switch
    {
        "Narrow" => 760,
        "Medium" => 1000,
        _ => 1280,
    };
}
