using System.Text.Json;
using Nomad.Core.Agents;
using Nomad.Core.Codex;
using Nomad.Core.Sessions;

namespace Nomad.Core.Tests;

/// <summary>Codex's app-server messages against turns captured from codex-cli 0.163.0 (see Fixtures/codex).</summary>
public class CodexTests
{
    private const string Cwd = @"C:\Users\Admin\AppData\Local\Temp\opencode\nomad\cwd";

    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", "codex", name);

    private static (List<AgentEvent> Events, CodexTranslator Translator) Replay(string name)
    {
        var lines = File.ReadAllLines(Fixture(name));
        var threadId = JsonDocument.Parse(lines.First(l => l.Contains("\"thread/started\""))).RootElement
            .GetProperty("params").GetProperty("thread").GetProperty("id").GetString()!;
        var translator = new CodexTranslator(threadId, Cwd) { Model = "gpt-6.1-sol" };
        translator.BeginTurn();
        var events = new List<AgentEvent>();
        foreach (var line in lines)
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            var method = root.GetProperty("method").GetString()!;
            var parameters = root.TryGetProperty("params", out var p) ? p : default;
            if (root.TryGetProperty("id", out var id))
            {
                events.AddRange(translator.Request(id, method, parameters, out var unsupported));
                Assert.False(unsupported);
            }
            else
            {
                events.AddRange(translator.Handle(method, parameters));
            }
        }

        return (events, translator);
    }

    [Fact]
    public void CommandTurnRaisesClaudeShapedEvents()
    {
        var (events, translator) = Replay("command.jsonl");

        // Every command ran through PowerShell; the card shows what ran inside the wrapper.
        var calls = events.OfType<ToolCallEvent>().ToList();
        Assert.Equal(8, calls.Count);
        Assert.All(calls, c => Assert.Equal("PowerShell", c.Name));
        Assert.Equal("Get-ChildItem -Force; Get-Content -LiteralPath notes.txt", calls[0].Input.GetProperty("command").GetString());

        // Each asked first (Manual), for the call already on screen.
        var asks = events.OfType<PermissionRequestedEvent>().Select(p => p.Request).ToList();
        Assert.Equal(8, asks.Count);
        Assert.Equal(calls[0].ToolUseId, asks[0].ToolUseId);
        Assert.Equal("PowerShell", asks[0].ToolName);
        Assert.Contains(asks, a => a.Description?.Contains("hello.py") == true);

        // The capture answered each itself: the server's "resolved" withdraws each card.
        Assert.Equal(8, events.OfType<PermissionCancelledEvent>().Count());
        Assert.Empty(translator.Pending);

        var results = events.OfType<ToolResultEvent>().ToList();
        Assert.Equal(8, results.Count);
        Assert.True(results[0].IsError);
        Assert.False(results[1].IsError);
        Assert.Contains("notes.txt", results[1].Text);

        // Commentary between the commands is text, as Claude's is; the last message is the answer.
        var texts = events.OfType<BlockCompletedEvent>().Where(b => b.Kind == AgentBlockKind.Text).ToList();
        Assert.Equal(3, texts.Count);
        Assert.Contains("hello", texts[^1].Text);
        Assert.Contains(events, e => e is BlockCompletedEvent { Kind: AgentBlockKind.Thinking, Text: "**Exploring Python for C files.**" });

        var done = Assert.Single(events.OfType<TurnCompletedEvent>()).Result;
        Assert.False(done.IsError);
        Assert.False(done.Interrupted);
        Assert.Equal(42380, done.DurationMs);
        Assert.Equal(787, done.OutputTokens);
        Assert.Equal(156928, done.CacheReadTokens);
        Assert.Equal(170218 - 156928, done.InputTokens);
        Assert.Equal(19821 + 24, done.ContextTokens);
        Assert.False(translator.IsTurnRunning);
        Assert.Equal(475000, CodexServer.ContextWindows["gpt-6.1-sol"]);
    }

    [Fact]
    public void FileChangeBecomesAnEditAndAWrite()
    {
        var (events, _) = Replay("edit.jsonl");

        // It read the file (Codex parsed that command as a read), looked around, patched, and checked.
        Assert.Equal(["Read", "PowerShell", "Edit", "Write", "PowerShell"], events.OfType<ToolCallEvent>().Select(c => c.Name));
        Assert.Equal(Path.Combine(Cwd, "notes.txt"), events.OfType<ToolCallEvent>().First().Input.GetProperty("file_path").GetString());
        var calls = events.OfType<ToolCallEvent>().Where(c => c.Name is "Edit" or "Write").ToList();
        var edit = calls[0].Input;
        Assert.Equal(Path.Combine(Cwd, "notes.txt"), edit.GetProperty("file_path").GetString());
        Assert.Equal("world", edit.GetProperty("old_string").GetString());
        Assert.Equal("there", edit.GetProperty("new_string").GetString());
        Assert.Equal("- Review notes.\n- Plan next steps.\n", calls[1].Input.GetProperty("content").GetString());

        // One approval for the item: its first change's card, naming the rest.
        var ask = Assert.Single(events.OfType<PermissionRequestedEvent>()).Request;
        Assert.Equal("Edit", ask.ToolName);
        Assert.Equal(calls[0].ToolUseId, ask.ToolUseId);
        Assert.Equal("Also changes todo.md", ask.Description);

        var results = events.OfType<ToolResultEvent>().Where(r => calls.Any(c => c.ToolUseId == r.ToolUseId)).ToList();
        Assert.Equal(2, results.Count);
        Assert.All(events.OfType<ToolResultEvent>(), r => Assert.False(r.IsError));
        var patch = results[0].Structured.GetProperty("structuredPatch")[0].GetProperty("lines");
        Assert.Equal(["-world", "+there"], patch.EnumerateArray().Select(l => l.GetString()));
        Assert.Equal("create", results[1].Structured.GetProperty("type").GetString());
    }

    [Fact]
    public void ApprovalRepliesTakeCodexDecisions()
    {
        var (_, translator) = Replay("edit.jsonl");
        using var doc = JsonDocument.Parse("""{"itemId":"x","threadId":"t"}""");
        var pending = new CodexPendingRequest(JsonDocument.Parse("3").RootElement, "item/fileChange/requestApproval",
            new PermissionRequest { RequestId = "codex-3", ToolName = "Edit" }, doc.RootElement);

        Assert.Equal("accept", CodexTranslator.Reply(pending, new PermissionDecision(PermissionChoice.Allow))["decision"]!.GetValue<string>());
        Assert.Equal("acceptForSession", CodexTranslator.Reply(pending, new PermissionDecision(PermissionChoice.AllowForSession))["decision"]!.GetValue<string>());
        Assert.Equal("decline", CodexTranslator.Reply(pending, new PermissionDecision(PermissionChoice.Deny, "no"))["decision"]!.GetValue<string>());
        Assert.False(translator.IsTurnRunning);
    }

    [Fact]
    public void QuestionsRoundTripAsAskUserQuestion()
    {
        var translator = new CodexTranslator("t", Cwd);
        translator.BeginTurn();
        using var doc = JsonDocument.Parse("""
            {"threadId":"t","turnId":"u","itemId":"i","isBlocking":true,"autoResolutionMs":null,
             "questions":[{"id":"q1","header":"Lang","question":"Which language?","isOther":true,"isSecret":false,
                           "options":[{"label":"C#","description":"dotnet"},{"label":"Rust","description":"cargo"}]}]}
            """);
        using var id = JsonDocument.Parse("5");
        var asked = Assert.Single(translator.Request(id.RootElement, "item/tool/requestUserInput", doc.RootElement, out _).OfType<PermissionRequestedEvent>()).Request;
        Assert.Equal("AskUserQuestion", asked.ToolName);
        Assert.Equal("Which language?", asked.Input.GetProperty("questions")[0].GetProperty("question").GetString());

        Assert.True(translator.TakePending(asked.RequestId, out var pending));
        using var answer = JsonDocument.Parse("""{"answers":{"Which language?":"Rust"}}""");
        var reply = CodexTranslator.Reply(pending!, new PermissionDecision(PermissionChoice.Allow, null, answer.RootElement.Clone()));
        Assert.Equal("Rust", reply["answers"]!["q1"]!["answers"]![0]!.GetValue<string>());
    }

    [Fact]
    public void HistoryRebuildsTheThread()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Fixture("edit.thread.json")));
        var thread = doc.RootElement.GetProperty("result").GetProperty("thread");
        var history = CodexHistory.FromThread(thread, thread.GetProperty("id").GetString()!, null);

        Assert.Equal(Cwd, history.WorkingDirectory);
        Assert.Equal("gpt-6.1-sol", history.Model);
        var prompt = Assert.IsType<HistoryUserMessage>(history.Items[0]);
        Assert.StartsWith("Edit notes.txt", prompt.Text);
        Assert.Equal(prompt.Uuid, prompt.ParentUuid);
        Assert.Equal(["Read", "PowerShell", "Edit", "Write", "PowerShell"], history.Items.OfType<HistoryToolCall>().Select(c => c.Name));
        Assert.Equal(5, history.Items.OfType<HistoryToolResult>().Count());
        Assert.StartsWith("Used `apply_patch`", history.Items.OfType<HistoryAssistantText>().Last().Text);
    }

    [Theory]
    [InlineData("\"C:\\\\WINDOWS\\\\System32\\\\WindowsPowerShell\\\\v1.0\\\\powershell.exe\" -Command 'Get-ChildItem'", "powershell", "Get-ChildItem")]
    [InlineData("/bin/bash -lc 'ls -la'", "bash", "ls -la")]
    [InlineData("git status", "", "git status")]
    public void UnwrapsTheShell(string command, string shell, string inner)
    {
        var (s, c) = CodexTools.Unwrap(command);
        Assert.Equal(shell, s);
        Assert.Equal(inner, c);
    }
}
