using System.Text.Json;
using System.Text.Json.Nodes;
using Nomad.Core.Agents;

namespace Nomad.Core.Tests;

[Collection(nameof(ChatToolsTests))]
public sealed class AgentToolsTests : IDisposable
{
    private sealed class FakeDesk(string folder) : IAgentDesk
    {
        public List<(string Key, string Path, string? Caption)> Files { get; } = [];

        public List<(string Key, string Title, string Message)> Notes { get; } = [];

        public Task<ChatInfo?> FindAsync(string? callerKey) =>
            Task.FromResult(callerKey == "me" ? new ChatInfo("s1", "Desk test", "Antigravity", folder, "working") : null);

        public Task ShowFileAsync(string callerKey, string path, string? caption)
        {
            Files.Add((callerKey, path, caption));
            return Task.CompletedTask;
        }

        public Task<string> NotifyAsync(string callerKey, string title, string message)
        {
            Notes.Add((callerKey, title, message));
            return Task.FromResult("the test.");
        }
    }

    private readonly string _root = Path.Combine(AppContext.BaseDirectory, "agent-tools-test-" + Guid.NewGuid().ToString("N"));
    private readonly string _folder;
    private readonly string _keptRoot = AgentTools.KeptRoot;
    private readonly string _logPath = AgentToolLog.FilePath;
    private readonly FakeDesk _desk;

    public AgentToolsTests()
    {
        _folder = Path.Combine(_root, "chat");
        Directory.CreateDirectory(_folder);
        _desk = new FakeDesk(_folder);
        AgentTools.Desk = _desk;
        AgentTools.KeptRoot = Path.Combine(_root, "kept");
        AgentToolLog.FilePath = Path.Combine(_root, "agent-tools.log");
        AgentTools.ListMonitors = () => [new MonitorInfo(1, -1920, 0, 1920, 1080, false), new MonitorInfo(2, 0, 0, 1920, 1080, true)];
        AgentTools.Capture = (_, _, w, h) => DesktopCapture.EncodePng(new byte[w * h * 4], w, h);
    }

    public void Dispose()
    {
        AgentTools.Desk = null;
        AgentTools.KeptRoot = _keptRoot;
        AgentToolLog.FilePath = _logPath;
        AgentTools.ListMonitors = DesktopCapture.Monitors;
        AgentTools.Capture = DesktopCapture.CapturePng;
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task TheBridgeListsThemAndClaudeDoesNotGetThem()
    {
        var bridge = await ChatTools.HandleAsync(Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}"""), "me");
        var names = bridge!["result"]!["tools"]!.AsArray().Select(t => t!["name"]!.GetValue<string>()).ToList();
        Assert.Equal(["list_chats", "send_to_chat", "take_screenshot", "run_on_desktop", "send_file", "notify"], names);

        var claude = await new NomadSessionServer(".", "me").HandleAsync(Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}"""));
        var claudeNames = claude!["result"]!["tools"]!.AsArray().Select(t => t!["name"]!.GetValue<string>()).ToList();
        Assert.DoesNotContain("take_screenshot", claudeNames);
        Assert.DoesNotContain("send_file", claudeNames);
    }

    [Fact]
    public async Task AChatNomadDidNotStartIsRefused()
    {
        var response = await ChatTools.HandleAsync(Call("notify", new JsonObject { ["title"] = "t", ["message"] = "m" }), "stranger");

        Assert.True(IsError(response));
        Assert.Empty(_desk.Notes);
    }

    [Fact]
    public async Task WithoutTheAppTheToolsAreUnavailable()
    {
        AgentTools.Desk = null;
        var response = await ChatTools.HandleAsync(Call("take_screenshot", new JsonObject()), "me");

        Assert.True(IsError(response));
        Assert.Contains("not available", Text(response));
    }

    [Fact]
    public async Task AScreenshotOfEveryMonitorIsSavedAndReturned()
    {
        var response = await ChatTools.HandleAsync(Call("take_screenshot", new JsonObject()), "me");

        Assert.False(IsError(response));
        var text = Text(response);
        Assert.Contains("all 2 monitors (3840x1080 pixels, top left at -1920,0", text);
        Assert.Contains("- 2: 1920x1080 at 0,0 (primary)", text);
        var path = Path.Combine(_root, "kept", "screenshots");
        var file = Assert.Single(Directory.GetFiles(path, "screenshot-*.png"));
        Assert.Contains(file, text);
        Assert.Equal([0x89, 0x50, 0x4E, 0x47], File.ReadAllBytes(file)[..4]);

        var image = response!["result"]!["content"]![1]!;
        Assert.Equal("image", image["type"]!.GetValue<string>());
        Assert.Equal("image/png", image["mimeType"]!.GetValue<string>());
        Assert.Equal(File.ReadAllBytes(file), Convert.FromBase64String(image["data"]!.GetValue<string>()));
    }

    [Fact]
    public async Task AScreenshotOfOneMonitorUsesItsBounds()
    {
        (int, int, int, int)? asked = null;
        AgentTools.Capture = (l, t, w, h) =>
        {
            asked = (l, t, w, h);
            return DesktopCapture.EncodePng(new byte[w * h * 4], w, h);
        };

        var response = await ChatTools.HandleAsync(Call("take_screenshot", new JsonObject { ["monitor"] = 1 }), "me");

        Assert.False(IsError(response));
        Assert.Equal((-1920, 0, 1920, 1080), asked);
        Assert.Contains("monitor 1", Text(response));
    }

    [Fact]
    public async Task AnUnknownMonitorIsAnErrorListingTheMonitors()
    {
        var response = await ChatTools.HandleAsync(Call("take_screenshot", new JsonObject { ["monitor"] = 5 }), "me");

        Assert.True(IsError(response));
        Assert.Contains("There is no monitor 5", Text(response));
        Assert.Contains("- 1: 1920x1080 at -1920,0", Text(response));
    }

    [Fact]
    public async Task ACaptureThatFailsIsAnError()
    {
        AgentTools.Capture = (_, _, _, _) => throw new InvalidOperationException("The screen could not be read.");

        var response = await ChatTools.HandleAsync(Call("take_screenshot", new JsonObject()), "me");

        Assert.True(IsError(response));
        Assert.Contains("The screen could not be read.", Text(response));
    }

    [Fact]
    public void ThePngEncoderWritesAValidImage()
    {
        // A 2x1 image: one red pixel, one blue (BGRA in, RGB out).
        var png = DesktopCapture.EncodePng([0, 0, 255, 255, 255, 0, 0, 255], 2, 1);

        Assert.Equal([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A], png[..8]);
        Assert.Equal("IHDR", System.Text.Encoding.ASCII.GetString(png, 12, 4));
        Assert.Equal(2, System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16)));
        Assert.Equal(1, System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20)));
        Assert.Equal("IEND", System.Text.Encoding.ASCII.GetString(png, png.Length - 8, 4));
        // The IHDR chunk's CRC, as any PNG reader checks it.
        Assert.Equal(0x7B40E8DDu, System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(29)));
    }

    [Fact]
    public async Task SendFileKeepsACopyAndShowsIt()
    {
        var source = Path.Combine(_folder, "report.txt");
        await File.WriteAllTextAsync(source, "hello");

        var response = await ChatTools.HandleAsync(Call("send_file", new JsonObject { ["path"] = "report.txt", ["caption"] = "The report" }), "me");

        Assert.False(IsError(response));
        var (key, kept, caption) = Assert.Single(_desk.Files);
        Assert.Equal("me", key);
        Assert.Equal("The report", caption);
        Assert.StartsWith(Path.Combine(_root, "kept", "sent"), kept);
        Assert.Equal("report.txt", Path.GetFileName(kept));
        Assert.Equal("hello", await File.ReadAllTextAsync(kept));
        Assert.EndsWith(AgentTools.KeptMarker + kept, Text(response));
    }

    [Fact]
    public async Task SendFileTwiceKeepsBothCopies()
    {
        var source = Path.Combine(_folder, "a.txt");
        await File.WriteAllTextAsync(source, "x");

        await ChatTools.HandleAsync(Call("send_file", new JsonObject { ["path"] = source }), "me");
        await ChatTools.HandleAsync(Call("send_file", new JsonObject { ["path"] = source }), "me");

        Assert.Equal(2, _desk.Files.Select(f => f.Path).Distinct().Count());
    }

    [Theory]
    [InlineData(null, "Give the path")]
    [InlineData("missing.txt", "No file at")]
    [InlineData(".", "is a folder")]
    public async Task SendFileRefusesWhatIsNotAFile(string? path, string expected)
    {
        var arguments = new JsonObject();
        if (path is not null) arguments["path"] = path;

        var response = await ChatTools.HandleAsync(Call("send_file", arguments), "me");

        Assert.True(IsError(response));
        Assert.Contains(expected, Text(response));
        Assert.Empty(_desk.Files);
    }

    [Fact]
    public async Task SendFileRefusesAFileOverTheCap()
    {
        var source = Path.Combine(_folder, "big.bin");
        await using (var stream = File.Create(source))
        {
            stream.SetLength(AgentTools.MaxFileBytes + 1);
        }

        var response = await ChatTools.HandleAsync(Call("send_file", new JsonObject { ["path"] = source }), "me");

        Assert.True(IsError(response));
        Assert.Contains("up to 100 MB", Text(response));
        Assert.Empty(_desk.Files);
    }

    [Fact]
    public async Task NotifyReachesTheAppAndSaysWhere()
    {
        var response = await ChatTools.HandleAsync(Call("notify", new JsonObject { ["title"] = "Done", ["message"] = "The build passed." }), "me");

        Assert.False(IsError(response));
        Assert.Equal([("me", "Done", "The build passed.")], _desk.Notes);
        Assert.Equal(AgentTools.NotifiedMarker + "the test.", Text(response));
    }

    [Fact]
    public async Task NotifyNeedsATitleAndAMessage()
    {
        var response = await ChatTools.HandleAsync(Call("notify", new JsonObject { ["title"] = "Done" }), "me");

        Assert.True(IsError(response));
        Assert.Empty(_desk.Notes);
    }

    [Theory]
    [InlineData(null, null, "Give the command")]
    [InlineData("Write-Output hi", 0, "timeout_seconds must be")]
    [InlineData("Write-Output hi", "soon", "timeout_seconds must be")]
    public async Task RunOnDesktopChecksItsArguments(string? command, object? timeout, string expected)
    {
        var arguments = new JsonObject();
        if (command is not null) arguments["command"] = command;
        if (timeout is int seconds) arguments["timeout_seconds"] = seconds;
        if (timeout is string text) arguments["timeout_seconds"] = text;

        var response = await ChatTools.HandleAsync(Call("run_on_desktop", arguments), "me");

        Assert.True(IsError(response));
        Assert.Contains(expected, Text(response));
    }

    [Fact]
    public async Task RunOnDesktopRefusesAMissingFolder()
    {
        var response = await ChatTools.HandleAsync(Call("run_on_desktop", new JsonObject { ["command"] = "Write-Output hi", ["working_folder"] = "nope" }), "me");

        Assert.True(IsError(response));
        Assert.Contains("does not exist", Text(response));
    }

    [Fact]
    public async Task RunOnDesktopReportsExitCodeOutputAndErrorsAndIsLogged()
    {
        if (!OperatingSystem.IsWindows()) return;

        var response = await ChatTools.HandleAsync(Call("run_on_desktop", new JsonObject
        {
            ["command"] = "Write-Output (Get-Location).Path; [Console]::Error.WriteLine('trouble'); exit 3",
        }), "me");

        var text = Text(response);
        Assert.True(IsError(response));
        Assert.StartsWith("Exit code: 3", text);
        Assert.Contains("Output:\n" + _folder, text);
        Assert.True(text.Contains("Errors:\ntrouble"), text);
        var log = await File.ReadAllTextAsync(Path.Combine(_root, "agent-tools.log"));
        Assert.Contains("run_on_desktop for \"Desk test\"", log);
        Assert.Contains("exit code 3", log);
    }

    [Fact]
    public async Task RunOnDesktopStopsACommandAtItsTimeLimit()
    {
        if (!OperatingSystem.IsWindows()) return;

        var started = DateTime.UtcNow;
        var response = await ChatTools.HandleAsync(Call("run_on_desktop", new JsonObject { ["command"] = "Start-Sleep 30", ["timeout_seconds"] = 1 }), "me");

        Assert.True(IsError(response));
        Assert.StartsWith("Stopped after 1 seconds", Text(response));
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(20));
    }

    [Fact]
    public void PowerShellErrorRecordsBecomeText()
    {
        const string stderr = "#< CLIXML\r\n<Objs Version=\"1.1.0.1\" xmlns=\"http://schemas.microsoft.com/powershell/2004/04\">" +
            "<Obj S=\"progress\" RefId=\"0\"><TN RefId=\"0\"><T>System.Object</T></TN></Obj>" +
            "<S S=\"Error\">Cannot find path &apos;X&apos;_x000D__x000A_</S></Objs>";

        Assert.Equal("Cannot find path 'X'", DesktopCommand.PlainErrors(stderr));
        Assert.Equal("plain", DesktopCommand.PlainErrors("plain"));
    }

    [Fact]
    public void LongOutputKeepsItsStartAndEnd()
    {
        var text = "A" + new string('x', DesktopCommand.MaxStreamChars * 2) + "Z";
        var clipped = DesktopCommand.Clip(text);

        Assert.StartsWith("A", clipped);
        Assert.EndsWith("Z", clipped);
        Assert.Contains("characters cut", clipped);
        Assert.True(clipped.Length < DesktopCommand.MaxStreamChars + 100);
    }

    [Fact]
    public void CodexGetsTheBridgeAsAThreadMcpServer()
    {
        ChatBridge.Listen();
        var config = ChatBridge.CodexConfig("key1");

        var server = config!["mcp_servers.nomad_chats"]!;
        Assert.Equal(Environment.ProcessPath, server["command"]!.GetValue<string>());
        Assert.Equal(["--chat-bridge", ChatBridge.PipeName!, "key1"], server["args"]!.AsArray().Select(a => a!.GetValue<string>()));
        Assert.True(server["tool_timeout_sec"]!.GetValue<int>() > DesktopCommand.MaxTimeout.TotalSeconds);
    }

    private static JsonElement Call(string name, JsonObject arguments) => Parse(new JsonObject
    {
        ["jsonrpc"] = "2.0",
        ["id"] = 7,
        ["method"] = "tools/call",
        ["params"] = new JsonObject { ["name"] = name, ["arguments"] = arguments },
    }.ToJsonString());

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    private static string Text(JsonObject? response) => response!["result"]!["content"]![0]!["text"]!.GetValue<string>();

    private static bool IsError(JsonObject? response) => response!["result"]!["isError"]!.GetValue<bool>();
}
