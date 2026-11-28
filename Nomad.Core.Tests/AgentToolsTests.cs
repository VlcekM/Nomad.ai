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

        public string? Mode { get; set; } = "bypassPermissions";

        public CancellationToken Stopped { get; set; }

        public Task<DeskChat?> FindAsync(string? callerKey) =>
            Task.FromResult(callerKey == "me" ? new DeskChat(new ChatInfo("s1", "Desk test", "Antigravity", folder, "working"), Mode, Stopped) : null);

        public Task ShowFileAsync(string callerKey, string path, string? caption)
        {
            lock (Files) Files.Add((callerKey, path, caption));
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

    [Theory]
    [InlineData("default", "Manual")]
    [InlineData("plan", "Plan")]
    [InlineData("acceptEdits", "Accept edits")]
    [InlineData("auto", "Auto")]
    [InlineData(null, "unknown")]
    public async Task RunOnDesktopIsRefusedUnlessTheChatBypassesPermissions(string? mode, string label)
    {
        _desk.Mode = mode;
        var started = 0;
        void Count(int _) => Interlocked.Increment(ref started);
        DesktopCommand.Started += Count;
        try
        {
            var response = await ChatTools.HandleAsync(Call("run_on_desktop", new JsonObject { ["command"] = "Write-Output hi" }), "me");

            Assert.True(IsError(response));
            Assert.Contains($"not available in this chat's permission mode, {label}.", Text(response));
            Assert.Contains("Bypass permissions", Text(response));
            Assert.Equal(0, started);
        }
        finally
        {
            DesktopCommand.Started -= Count;
        }
    }

    [Theory]
    [InlineData("default")]
    [InlineData("plan")]
    public async Task TheOtherToolsWorkInEveryMode(string mode)
    {
        _desk.Mode = mode;
        await File.WriteAllTextAsync(Path.Combine(_folder, "a.txt"), "x");

        Assert.False(IsError(await ChatTools.HandleAsync(Call("take_screenshot", new JsonObject()), "me")));
        Assert.False(IsError(await ChatTools.HandleAsync(Call("send_file", new JsonObject { ["path"] = "a.txt" }), "me")));
        Assert.False(IsError(await ChatTools.HandleAsync(Call("notify", new JsonObject { ["title"] = "t", ["message"] = "m" }), "me")));
    }

    [Fact]
    public void OnlyBypassPermissionsAllowsDesktopCommands()
    {
        Assert.True(AgentTools.AllowsDesktopCommands("bypassPermissions"));
        foreach (var mode in new[] { "default", "acceptEdits", "auto", "plan", "dontAsk", "", null })
        {
            Assert.False(AgentTools.AllowsDesktopCommands(mode));
        }
    }

    [Fact]
    public async Task StoppingTheTurnStopsTheCommandAndWhatItStarted()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var stop = new CancellationTokenSource();
        _desk.Stopped = stop.Token;
        var marker = Path.Combine(_root, "child.pid");
        var call = ChatTools.HandleAsync(Call("run_on_desktop", new JsonObject
        {
            ["command"] = ChildCommand(marker) + "; Start-Sleep 60",
            ["timeout_seconds"] = 120,
        }), "me");

        var child = await WaitForPidAsync(marker);
        var started = DateTime.UtcNow;
        await stop.CancelAsync();
        var response = await call;

        Assert.True(IsError(response));
        Assert.StartsWith("Stopped before it finished", Text(response));
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(20));
        Assert.True(HasExited(child), "the command's child is still running");
    }

    [Fact]
    public async Task ACancelledCallStopsTheCommand()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var started = DateTime.UtcNow;
        var result = await AgentTools.CallAsync("run_on_desktop",
            Parse("""{"name":"run_on_desktop","arguments":{"command":"Start-Sleep 60","timeout_seconds":120}}"""), "me", cancel.Token);

        Assert.True(result["isError"]!.GetValue<bool>());
        Assert.StartsWith("Stopped before it finished", result["content"]![0]!["text"]!.GetValue<string>());
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(20));
    }

    [Fact]
    public async Task AProgramTheCommandOpensOutlivesItAndIsNotInNomadsJob()
    {
        if (!OperatingSystem.IsWindows()) return;

        // Nomad's job exists once any process is put in it; without one, the check below would prove nothing.
        using var member = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", "/c ping -n 30 127.0.0.1 >nul")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
        })!;
        Nomad.Core.Process.ChildProcessJob.TryAssign(member);
        Assert.Contains(member.Id, Nomad.Core.Process.ChildProcessJob.ProcessIds());

        var marker = Path.Combine(_root, "child.pid");
        var shell = 0;
        void Remember(int pid) => shell = pid;
        DesktopCommand.Started += Remember;
        int child;
        try
        {
            var response = await ChatTools.HandleAsync(Call("run_on_desktop", new JsonObject { ["command"] = ChildCommand(marker) }), "me");
            Assert.False(IsError(response), Text(response));
            child = await WaitForPidAsync(marker);
        }
        finally
        {
            DesktopCommand.Started -= Remember;
        }

        try
        {
            Assert.False(HasExited(child), "the program the command opened ended with it");
            Assert.NotEqual(0, shell);
            Assert.DoesNotContain(child, Nomad.Core.Process.ChildProcessJob.ProcessIds());
            Assert.DoesNotContain(shell, Nomad.Core.Process.ChildProcessJob.ProcessIds());
        }
        finally
        {
            // The test's own child, by the id it wrote.
            try
            {
                using var process = System.Diagnostics.Process.GetProcessById(child);
                process.Kill();
            }
            catch (ArgumentException)
            {
            }

            member.Kill(entireProcessTree: true);
        }
    }

    [Fact]
    public async Task TheBridgeCancelsACallOnNotificationsCancelledAndAnswersNothing()
    {
        if (!OperatingSystem.IsWindows()) return;

        var (client, serving, pids) = await ConnectAsync();
        await using var _ = client;
        using var reader = new StreamReader(client);
        var writer = new StreamWriter(client) { AutoFlush = true, NewLine = "\n" };
        await writer.WriteLineAsync("me");
        await writer.WriteLineAsync(Call("run_on_desktop", new JsonObject { ["command"] = "Start-Sleep 60", ["timeout_seconds"] = 120 }, id: 5).GetRawText());
        var shell = await pids.Task.WaitAsync(TimeSpan.FromSeconds(30));

        await writer.WriteLineAsync("""{"jsonrpc":"2.0","method":"notifications/cancelled","params":{"requestId":5,"reason":"test"}}""");
        await WaitUntilAsync(() => HasExited(shell));
        await writer.WriteLineAsync("""{"jsonrpc":"2.0","id":6,"method":"ping"}""");

        // The cancelled call gets no answer: the first line back is the ping's.
        var answer = JsonDocument.Parse((await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(30)))!).RootElement;
        Assert.Equal(6, answer.GetProperty("id").GetInt32());
        client.Close();
        await serving.WaitAsync(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task TheBridgeGoingAwayStopsItsCommand()
    {
        if (!OperatingSystem.IsWindows()) return;

        var (client, serving, pids) = await ConnectAsync();
        var writer = new StreamWriter(client) { AutoFlush = true, NewLine = "\n" };
        await writer.WriteLineAsync("me");
        await writer.WriteLineAsync(Call("run_on_desktop", new JsonObject { ["command"] = "Start-Sleep 60", ["timeout_seconds"] = 120 }).GetRawText());
        var shell = await pids.Task.WaitAsync(TimeSpan.FromSeconds(30));

        await client.DisposeAsync();
        await serving.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(HasExited(shell));
    }

    [Fact]
    public async Task SendFileCallsInTheSameMomentEachKeepTheirOwnCopy()
    {
        var source = Path.Combine(_folder, "same.txt");
        await File.WriteAllTextAsync(source, "x");

        var responses = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ =>
            Task.Run(() => ChatTools.HandleAsync(Call("send_file", new JsonObject { ["path"] = source }), "me"))));

        Assert.All(responses, r => Assert.False(IsError(r), Text(r)));
        Assert.Equal(20, _desk.Files.Select(f => f.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public async Task ScreenshotsInTheSameMomentEachGetTheirOwnFile()
    {
        var responses = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ =>
            Task.Run(() => ChatTools.HandleAsync(Call("take_screenshot", new JsonObject()), "me"))));

        Assert.All(responses, r => Assert.False(IsError(r), Text(r)));
        Assert.Equal(10, Directory.GetFiles(Path.Combine(_root, "kept", "screenshots"), "screenshot-*.png").Length);
    }

    [Fact]
    public void TheSentFileIndexFindsTheCopyEachCallMade()
    {
        var one = Path.Combine(_root, "kept", "sent", "1", "report.md");
        var two = Path.Combine(_root, "kept", "sent", "2", "report.md");
        var three = Path.Combine(_root, "kept", "sent", "3", "report.md");
        SentFileIndex.Record("chat-a", "call-1", one);
        SentFileIndex.Record("chat-a", "call-2", two);
        SentFileIndex.Record("chat-b", "call-1", three);

        Assert.Equal(one, SentFileIndex.Find("chat-a", "call-1"));
        Assert.Equal(two, SentFileIndex.Find("chat-a", "call-2"));
        Assert.Equal(three, SentFileIndex.Find("chat-b", "call-1"));
        Assert.Null(SentFileIndex.Find("chat-b", "call-2"));

        var saved = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(SentFileIndex.FolderPath, "chat-a.json")));
        Assert.Equal(two, saved!["call-2"]);
    }

    /// <summary>
    /// PowerShell that opens a hidden PowerShell sleeping for two minutes and writes that child's process id to the marker file.
    /// </summary>
    private static string ChildCommand(string marker) =>
        "$c = Start-Process powershell -WindowStyle Hidden -PassThru -ArgumentList '-NoProfile','-Command','Start-Sleep 120'; " +
        $"Set-Content -LiteralPath '{marker.Replace("'", "''")}' -Value $c.Id";

    private static async Task<int> WaitForPidAsync(string marker)
    {
        for (var i = 0; i < 300; i++)
        {
            try
            {
                if (File.Exists(marker) && int.TryParse((await File.ReadAllTextAsync(marker)).Trim(), out var pid)) return pid;
            }
            catch (IOException)
            {
                // Still being written.
            }

            await Task.Delay(100);
        }

        throw new TimeoutException("The command never started its child.");
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 300 && !condition(); i++) await Task.Delay(100);

        Assert.True(condition());
    }

    private static bool HasExited(int pid)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(pid);
            return process.HasExited;
        }
        catch (ArgumentException)
        {
            return true;
        }
    }

    /// <summary>
    /// A pipe served as Nomad serves a bridge process, its client end, and the id of the first PowerShell a call starts.
    /// </summary>
    private static async Task<(System.IO.Pipes.NamedPipeClientStream Client, Task Serving, TaskCompletionSource<int> Pids)> ConnectAsync()
    {
        var name = "nomad-test-" + Guid.NewGuid().ToString("N");
        var server = new System.IO.Pipes.NamedPipeServerStream(name, System.IO.Pipes.PipeDirection.InOut, 1,
            System.IO.Pipes.PipeTransmissionMode.Byte, System.IO.Pipes.PipeOptions.Asynchronous);
        var client = new System.IO.Pipes.NamedPipeClientStream(".", name, System.IO.Pipes.PipeDirection.InOut, System.IO.Pipes.PipeOptions.Asynchronous);
        var connecting = server.WaitForConnectionAsync();
        await client.ConnectAsync(10_000);
        await connecting;

        var pids = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        void First(int pid)
        {
            if (pids.TrySetResult(pid)) DesktopCommand.Started -= First;
        }

        DesktopCommand.Started += First;
        return (client, Task.Run(() => ChatBridge.ServeAsync(server)), pids);
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

    private static JsonElement Call(string name, JsonObject arguments, int id = 7) => Parse(new JsonObject
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id,
        ["method"] = "tools/call",
        ["params"] = new JsonObject { ["name"] = name, ["arguments"] = arguments },
    }.ToJsonString());

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    private static string Text(JsonObject? response) => response!["result"]!["content"]![0]!["text"]!.GetValue<string>();

    private static bool IsError(JsonObject? response) => response!["result"]!["isError"]!.GetValue<bool>();
}
