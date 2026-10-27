using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Nomad.App.Markdown;

namespace Nomad.App.Views;

/// <summary>
/// <c>--dev-sandbox composer</c>: the composer's text box on its own, to try Markdown and list writing without a
/// session. Enter "sends": the text is rendered above as the transcript would show it. Takes <c>--dev-compose</c>, <c>--dev-key</c>,
/// <c>--dev-size</c> and <c>--dev-screenshot</c>.
/// </summary>
public sealed class ComposerSandbox : Window
{
    private const string Sample =
        "## Plan\n" +
        "Refactor the **settings** page and keep *every* `NomadSettings` key.\n" +
        "- Move the fonts to their own section\n" +
        "- Rename ~~Interface~~ to **Appearance**\n" +
        "  - keep the old key working\n" +
        "1. Write the migration\n" +
        "2. Test it on [the docs](https://example.com)\n" +
        "> Don't touch the updater.\n" +
        "```\nvar x = 1;\n```";

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    private readonly StackPanel _sent = new() { Spacing = 14 };
    private readonly ScrollViewer _scroller;
    private readonly TextBox _box;

    public ComposerSandbox()
    {
        var dev = Program.Dev;
        Title = "Composer sandbox";
        Width = dev.Size?.Width ?? 760;
        Height = dev.Size?.Height ?? 560;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        this[!BackgroundProperty] = new DynamicResourceExtension("BgMainBrush");

        _box = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MaxHeight = 190,
            Margin = new Thickness(8, 11),
            VerticalContentAlignment = VerticalAlignment.Top,
            Watermark = "Type Markdown…",
            Text = dev.Compose ?? Sample,
        };
        _box.Theme = (Avalonia.Styling.ControlTheme)Application.Current!.FindResource("MarkdownTextBox")!;
        _box[!TextBox.FontSizeProperty] = new DynamicResourceExtension("UiFontSizeLarge");
        ScrollViewer.SetVerticalScrollBarVisibility(_box, Avalonia.Controls.Primitives.ScrollBarVisibility.Auto);
        _box.AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);

        var bar = new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            MinHeight = 44,
            Cursor = new Cursor(StandardCursorType.Ibeam),
            Child = _box,
        };
        bar[!Border.BackgroundProperty] = new DynamicResourceExtension("BgInputBrush");
        bar[!Border.BorderBrushProperty] = new DynamicResourceExtension("BorderInputBrush");
        bar.PointerPressed += (_, _) => _box.Focus();

        var hint = new TextBlock
        {
            Text = "Enter sends (rendered above)  ·  Shift+Enter: new line, next list item  ·  Tab / Shift+Tab: nest  ·  Backspace after a marker: drop it",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(4, 8, 4, 0),
        };
        hint[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("TextMutedBrush");
        hint[!TextBlock.FontSizeProperty] = new DynamicResourceExtension("UiFontSizeSmall");

        _scroller = new ScrollViewer { Content = _sent, Margin = new Thickness(0, 0, 0, 14) };
        var root = new Grid { RowDefinitions = new RowDefinitions("*,Auto,Auto"), Margin = new Thickness(32, 24, 32, 20) };
        root.Children.Add(_scroller);
        Grid.SetRow(bar, 1);
        root.Children.Add(bar);
        Grid.SetRow(hint, 2);
        root.Children.Add(hint);
        Content = root;

        if (dev.ScreenshotPath is not null)
        {
            ShowActivated = false;
            ShowInTaskbar = false;
            if (TryGetPlatformHandle() is { } handle)
            {
                var cloak = 1;
                _ = DwmSetWindowAttribute(handle.Handle, 13, ref cloak, sizeof(int));
            }
        }

        Opened += (_, _) => _ = StartAsync();
    }

    private async Task StartAsync()
    {
        _box.Focus();
        _box.CaretIndex = _box.Text?.Length ?? 0;
        // --dev-key "text:`" types the text instead of pressing a key.
        if (Program.Dev.Key is var (typed, at) && typed.StartsWith("text:", StringComparison.Ordinal))
        {
            DispatcherTimer.RunOnce(() => _box.RaiseEvent(new TextInputEventArgs { RoutedEvent = TextInputEvent, Text = typed[5..] }),
                TimeSpan.FromSeconds(at));
        }
        else if (Program.Dev.Key is var (gesture, seconds))
        {
            var key = KeyGesture.Parse(gesture);
            DispatcherTimer.RunOnce(() => _box.RaiseEvent(new KeyEventArgs
            {
                RoutedEvent = KeyDownEvent,
                Key = key.Key,
                KeyModifiers = key.KeyModifiers,
            }), TimeSpan.FromSeconds(seconds));
        }

        if (Program.Dev.ScreenshotPath is not { } path)
        {
            return;
        }

        await Task.Delay(TimeSpan.FromSeconds(Program.Dev.AfterSeconds) + TimeSpan.FromMilliseconds(600));
        var scaling = RenderScaling;
        using var bitmap = new RenderTargetBitmap(
            new PixelSize((int)(Bounds.Width * scaling), (int)(Bounds.Height * scaling)), new Vector(96 * scaling, 96 * scaling));
        bitmap.Render(this);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        bitmap.Save(path);
        Close();
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || e.KeyModifiers != KeyModifiers.None || string.IsNullOrWhiteSpace(_box.Text))
        {
            return;
        }

        e.Handled = true;
        var message = new Border
        {
            Padding = new Thickness(14, 10),
            CornerRadius = new CornerRadius(10),
            Child = new MarkdownView { Markdown = _box.Text },
        };
        message[!Border.BackgroundProperty] = new DynamicResourceExtension("BgRaisedBrush");
        _sent.Children.Add(message);
        Controls.Entrance.Play(message);
        _box.Text = "";
        Dispatcher.UIThread.Post(() => _scroller.ScrollToEnd(), DispatcherPriority.Loaded);
    }
}
