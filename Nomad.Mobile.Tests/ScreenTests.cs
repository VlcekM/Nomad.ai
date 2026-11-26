using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Nomad.Hub.Contracts;
using Nomad.Mobile.Hub;
using Nomad.Mobile.ViewModels;
using Nomad.Mobile.Views;

[assembly: AvaloniaTestApplication(typeof(Nomad.Mobile.Tests.TestApp))]

namespace Nomad.Mobile.Tests;

/// <summary>The app itself, headless and drawn with Skia, so the screens render as they would on a phone.</summary>
public sealed class TestApp
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UseSkia()
        .WithInterFont()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

/// <summary>
/// Renders every screen in a phone-sized window, light and dark, and saves each as a PNG beside the tests (screens\), so a change
/// can be looked at without a phone. Each also checks that the screen shows what it should.
/// </summary>
[Collection("app services")]
public sealed class ScreenTests
{
    // A Galaxy S21 FE's screen in device-independent pixels.
    private const int Width = 412, Height = 892;

    public ScreenTests() => AppServices.Post = work => work();

    public static string ShotsFolder => Environment.GetEnvironmentVariable("NOMAD_MOBILE_SCREENS") is { Length: > 0 } dir
        ? dir
        : Path.Combine(AppContext.BaseDirectory, "screens");

    private static Window Show(object page, MainViewModel main, ThemeVariant theme)
    {
        Application.Current!.RequestedThemeVariant = theme;
        main.Page = page;
        var window = new Window { Width = Width, Height = Height, Content = new MainView { DataContext = main } };
        window.Show();
        return window;
    }

    private static void Save(Window window, string name, ThemeVariant theme)
    {
        // Two passes: layout settles (text measured, lists filled) before the frame that is kept.
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        window.CaptureRenderedFrame();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        var frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("Nothing rendered.");
        Directory.CreateDirectory(ShotsFolder);
        frame.Save(Path.Combine(ShotsFolder, $"{name}-{(theme == ThemeVariant.Dark ? "dark" : "light")}.png"));
        window.Close();
    }

    private static async Task<(MainViewModel, FakeHub)> SignedInAsync()
    {
        var hub = new FakeHub();
        hub.Board.AddRange(Samples.Board());
        var store = new MemorySecretStore();
        new Enrollment(new Uri("https://desk.example.ts.net/"), Guid.NewGuid(), "Pixel 8", "secret").Save(store);
        var main = new MainViewModel(store, hub);
        main.Start();
        await Task.Yield();
        return (main, hub);
    }

    private static T Find<T>(Control root, Func<T, bool> match) where T : Control =>
        root.GetVisualDescendants().OfType<T>().FirstOrDefault(match)
        ?? throw new Xunit.Sdk.XunitException($"No {typeof(T).Name} matched on the screen.");

    public static IEnumerable<object[]> Themes => [[ "dark" ], [ "light" ]];

    private static ThemeVariant Theme(string name) => name == "dark" ? ThemeVariant.Dark : ThemeVariant.Light;

    [AvaloniaTheory]
    [MemberData(nameof(Themes))]
    public void Connect_screen(string themeName)
    {
        var theme = Theme(themeName);
        var main = new MainViewModel(new MemorySecretStore(), new FakeHub());
        main.Start();
        main.Connect.Fill("https://desk.example.ts.net", "kuxZR6HOMWtNIVf1GN069W1w");
        main.Connect.DeviceName = "Pixel 8";
        var window = Show(main.Connect, main, theme);

        Find<TextBlock>(window, t => t.Text == "Connect to Nomad Hub");
        Assert.True(Find<Button>(window, b => b.Content as string == "Connect this phone").IsEffectivelyEnabled);
        Save(window, "1-connect", theme);
    }

    [AvaloniaTheory]
    [MemberData(nameof(Themes))]
    public void Connect_screen_with_a_problem(string themeName)
    {
        var theme = Theme(themeName);
        var main = new MainViewModel(new MemorySecretStore(), new FakeHub());
        main.Start();
        main.Connect.Fill("http://192.168.1.5:8080", "abc");
        main.Connect.Error = EnrollLink.CheckAddress("http://192.168.1.5:8080").Problem;
        var window = Show(main.Connect, main, theme);

        Find<TextBlock>(window, t => t.Text?.Contains("needs the hub over HTTPS", StringComparison.Ordinal) == true);
        Save(window, "1b-connect-problem", theme);
    }

    [AvaloniaTheory]
    [MemberData(nameof(Themes))]
    public async Task Chats_screen(string themeName)
    {
        var theme = Theme(themeName);
        var (main, _) = await SignedInAsync();
        var window = Show(main.Chats, main, theme);

        Find<TextBlock>(window, t => t.Text == "Update the README");
        Find<TextBlock>(window, t => t.Text == "Laptop");
        Save(window, "2-chats", theme);
    }

    [AvaloniaTheory]
    [MemberData(nameof(Themes))]
    public async Task Chats_screen_empty(string themeName)
    {
        var theme = Theme(themeName);
        var (main, hub) = await SignedInAsync();
        hub.RaiseBoard(new BoardDevice(Samples.Desk, "Desk PC", [], 99));
        hub.RaiseBoard(new BoardDevice(Samples.Laptop, "Laptop", [], 99));
        var window = Show(main.Chats, main, theme);

        Find<TextBlock>(window, t => t.Text?.Contains("No chats are shared", StringComparison.Ordinal) == true);
        Save(window, "2b-chats-empty", theme);
    }

    [AvaloniaTheory]
    [MemberData(nameof(Themes))]
    public async Task Chat_screen(string themeName)
    {
        var theme = Theme(themeName);
        var (main, hub) = await SignedInAsync();
        hub.Pages["d1"] = Samples.Page();
        main.OpenChatRow(main.Chats.Groups.SelectMany(g => g.Chats).Single(c => c.SessionId == "d1"));
        var chat = (ChatViewModel)main.Page;
        await chat.AttachAsync();
        var window = Show(chat, main, theme);

        Find<TextBlock>(window, t => t.Text == "Fix the parser bug");
        Find<TextBlock>(window, t => t.Text == "from Pixel 8");
        Save(window, "3-chat", theme);
    }

    [AvaloniaTheory]
    [MemberData(nameof(Themes))]
    public async Task Chat_screen_working_with_a_turn_streaming(string themeName)
    {
        var theme = Theme(themeName);
        var (main, hub) = await SignedInAsync();
        hub.Pages["d2"] = Samples.Page(state: "working");
        main.OpenChatRow(main.Chats.Groups.SelectMany(g => g.Chats).Single(c => c.SessionId == "d2"));
        var chat = (ChatViewModel)main.Page;
        await chat.AttachAsync();
        hub.RaiseRelay("claude-code", "d2", 0,
            Core.Hub.RelayCodec.Encode(new Core.Agents.AssistantMessageStartedEvent("m", null, null))!,
            Core.Hub.RelayCodec.Encode(new Core.Agents.TextDeltaEvent("m", 0, "The lexer fix is in. Now running the tests to make sure nothing else", null))!);
        chat.Draft = "Also check the Linux build";
        var window = Show(chat, main, theme);

        Assert.True(Find<Button>(window, b => b.Content as string == "Stop").IsVisible);
        Save(window, "3b-chat-working", theme);
    }

    [AvaloniaTheory]
    [MemberData(nameof(Themes))]
    public async Task Chat_screen_with_a_permission_card(string themeName)
    {
        var theme = Theme(themeName);
        var (main, hub) = await SignedInAsync();
        hub.Pages["d3"] = Samples.Page(withCard: true);
        main.OpenChatRow(main.Chats.Groups.SelectMany(g => g.Chats).Single(c => c.SessionId == "d3"));
        var chat = (ChatViewModel)main.Page;
        await chat.AttachAsync();
        var window = Show(chat, main, theme);

        Find<TextBlock>(window, t => t.Text == "Allow Bash?");
        Find<Button>(window, b => b.Content as string == "Allow for this session");
        Save(window, "4-permission", theme);
    }

    [AvaloniaTheory]
    [MemberData(nameof(Themes))]
    public async Task Chat_screen_with_a_question(string themeName)
    {
        var theme = Theme(themeName);
        var (main, hub) = await SignedInAsync();
        hub.Pages["d3"] = Samples.Page(withCard: true, question: true);
        main.OpenChatRow(main.Chats.Groups.SelectMany(g => g.Chats).Single(c => c.SessionId == "d3"));
        var chat = (ChatViewModel)main.Page;
        await chat.AttachAsync();
        chat.Card!.Questions[0].Options[0].IsSelected = true;
        var window = Show(chat, main, theme);

        Find<TextBlock>(window, t => t.Text == "Where should the fix go?");
        Save(window, "5-question", theme);
    }

    [AvaloniaTheory]
    [MemberData(nameof(Themes))]
    public async Task Settings_screen(string themeName)
    {
        var theme = Theme(themeName);
        var (main, _) = await SignedInAsync();
        var window = Show(new SettingsViewModel(main), main, theme);

        Find<TextBlock>(window, t => t.Text == "https://desk.example.ts.net");
        Find<TextBlock>(window, t => t.Text == "Desk PC");
        Save(window, "6-settings", theme);
    }
}

[CollectionDefinition("app services", DisableParallelization = true)]
public sealed class AppServicesCollection;
