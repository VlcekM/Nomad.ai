using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Primitives;
using Avalonia.Markup.Xaml;
using Nomad.App.Markdown;
using Nomad.App.ViewModels;
using Nomad.Core.Process;
using Nomad.Core.Settings;

namespace Nomad.App;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);

        // Tooltips sit just above their control (not at the pointer, where they would cover the next chip over).
        // Set as each one opens, unless the control chose a placement itself.
        ToolTip.ToolTipOpeningEvent.AddClassHandler<Control>((control, _) =>
        {
            if (!control.IsSet(ToolTip.PlacementProperty))
            {
                ToolTip.SetPlacement(control, PlacementMode.Top);
                ToolTip.SetVerticalOffset(control, -6);
            }
        });

        // A tooltip goes as soon as the pointer leaves its control, even onto the tooltip itself (which can cover the
        // next chip over): Avalonia would otherwise keep it open while the pointer is on it.
        Avalonia.Input.InputElement.PointerExitedEvent.AddClassHandler<Control>((control, _) =>
        {
            if (ToolTip.GetIsOpen(control))
            {
                ToolTip.SetIsOpen(control, false);
            }
        });
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var settings = Program.Settings;
            Infrastructure.ClaudeFonts.Register();
            Infrastructure.WorkingMarkSheets.LoadInBackground();

            // Notifications' app id; the click-through protocol only for a real start (never a scratch dev build).
            var realStart = !Program.Dev.IsActive;
            _ = Task.Run(() => Infrastructure.WindowsToast.Register(protocol: realStart));
            ApplyAppearance(settings);

            if (Program.Dev.Sandbox == "composer")
            {
                desktop.MainWindow = new Views.ComposerSandbox();
                base.OnFrameworkInitializationCompleted();
                return;
            }

            var vm = new MainViewModel(settings, Program.SettingsPath, Program.Dev);
            var window = new MainWindow(vm);
            desktop.MainWindow = window;
            desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
            desktop.Exit += (_, _) =>
            {
                ClaudeCliProcess.KillAllOwned();
                Nomad.Core.OpenCode.OpenCodeServer.KillShared();
            };

            // Launched again: show this window (from the tray too) rather than a second Nomad.ai.
            if (Program.Instance is { } instance)
            {
                instance.ShowRequested += sessionId => Avalonia.Threading.Dispatcher.UIThread.Post(() => window.BringToFront(sessionId));
                instance.Listen();
                desktop.ShutdownRequested += (_, _) => instance.StopListening();
                desktop.Exit += (_, _) => instance.StopListening();
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>Pushes the appearance settings into app resources and the markdown style.</summary>
    public static void ApplyAppearance(NomadSettings settings)
    {
        if (Current is not { } app)
        {
            return;
        }

        Set("UiFont", Fonts.Ui(settings.InterfaceFont));
        Set("MonoFont", Fonts.Code(settings.CodeFont));
        var uiSize = settings.InterfaceTextSize switch
        {
            "Small" => 12.0,
            "Large" => 14.0,
            _ => 13.0,
        };
        Set("UiFontSize", uiSize);
        Set("UiFontSizeSmall", uiSize - 1);
        Set("UiFontSizeTiny", uiSize - 2);
        Set("UiFontSizeLarge", uiSize + 1);
        Set("UiFontSizeTitle", uiSize + 3);
        Set("ColumnMaxWidth", ColumnWidth(settings.TranscriptWidth));

        var style = MarkdownStyle.From(settings.TextSize, settings.InterfaceFont, settings.CodeFont);
        Set("BodyFontSize", style.BodySize);
        Set("BodyLineHeight", style.LineHeight);
        Set("CodeFontSize", style.CodeSize);

        // Theme: the palette's resources follow at once; rendered markdown (brushes picked in code) is rebuilt.
        var theme = settings.Theme switch
        {
            "Light" => Avalonia.Styling.ThemeVariant.Light,
            "System" => Avalonia.Styling.ThemeVariant.Default,
            _ => Avalonia.Styling.ThemeVariant.Dark,
        };
        var themeChanged = app.RequestedThemeVariant != theme;
        if (themeChanged)
        {
            app.RequestedThemeVariant = theme;
        }

        themeChanged |= Infrastructure.Themes.Use(settings.ColorTheme);

        var old = MarkdownStyle.Current;
        if (themeChanged || old.BodySize != style.BodySize || old.CodeSize != style.CodeSize ||
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

    /// <summary>Re-renders markdown after the theme's colors changed (its brushes are picked in code).</summary>
    public static void RestyleMarkdown()
    {
        var old = MarkdownStyle.Current;
        MarkdownStyle.Current = new MarkdownStyle { BodySize = old.BodySize, CodeSize = old.CodeSize, BodyFont = old.BodyFont, CodeFont = old.CodeFont };
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
