using System.Text.Json;
using Nomad.Core.Agents;
using Nomad.Core.OpenCode;
using Nomad.Core.Sessions;

namespace Nomad.Core.Tests;

/// <summary>OpenCode's server events against turns captured from opencode 1.19.5 (see Fixtures/opencode).</summary>
public class OpenCodeTests
{
    private const string Cwd = @"R:\Mato\scratch\oc\work";

    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", "opencode", name);

    private static List<AgentEvent> Replay(string name, Action<OpenCodeTranslator>? begin = null)
    {
        var lines = File.ReadAllLines(Fixture(name));
        var sessionId = JsonDocument.Parse(lines.First(l => l.Contains("\"session.created\""))).RootElement
            .GetProperty("properties").GetProperty("sessionID").GetString()!;
        var translator = new OpenCodeTranslator(sessionId, Cwd);
        translator.BeginTurn();
        begin?.Invoke(translator);
        var events = new List<AgentEvent>();
        foreach (var line in lines)
        {
            using var doc = JsonDocument.Parse(line);
            events.AddRange(translator.Handle(doc.RootElement));
        }

        return events;
    }

    [Fact]
    public void ToolTurnRaisesClaudeShapedEvents()
    {
        var events = Replay("tool.jsonl");

        // The model first called write without a filePath (an error), then with one, then read the file.
        var calls = events.OfType<ToolCallEvent>().GroupBy(c => c.ToolUseId).Select(g => g.Last()).ToList();
        Assert.Equal(["Write", "Write", "Read"], calls.Select(c => c.Name));
        var write = calls[1];
        Assert.Equal(Path.Combine(Cwd, "hello.txt"), write.Input.GetProperty("file_path").GetString());
        Assert.Equal("hi\n", write.Input.GetProperty("content").GetString());

        var results = events.OfType<ToolResultEvent>().ToList();
        Assert.Equal(3, results.Count);
        Assert.True(results[0].IsError);
        Assert.Contains("filePath", results[0].Text);
        Assert.False(results[1].IsError);

        // OpenCode asks for "edit" while the write call runs: the card shows that call as it is.
        var permission = Assert.Single(events.OfType<PermissionRequestedEvent>()).Request;
        Assert.Equal("Write", permission.ToolName);
        Assert.Equal(write.ToolUseId, permission.ToolUseId);
        Assert.Equal(Path.Combine(Cwd, "hello.txt"), permission.Input.GetProperty("file_path").GetString());
        Assert.Equal("hi\n", permission.Input.GetProperty("content").GetString());

        // Our own reply comes back as permission.replied: nothing to withdraw then, since the test didn't answer it.
        Assert.Single(events.OfType<PermissionCancelledEvent>());

        var text = events.OfType<BlockCompletedEvent>().Last(b => b.Kind == AgentBlockKind.Text);
        Assert.StartsWith("It says **hi**", text.Text);
        Assert.Equal(
            text.Text,
            string.Concat(events.OfType<TextDeltaEvent>().Where(d => d.MessageId == text.MessageId && d.Index == text.Index).Select(d => d.Text)));
        Assert.Contains(events, e => e is ThinkingDeltaEvent);

        var done = Assert.Single(events.OfType<TurnCompletedEvent>()).Result;
        Assert.False(done.IsError);
        Assert.False(done.Interrupted);
        Assert.Equal(4, done.NumTurns);
        Assert.Equal(12337, done.ContextTokens);
    }

    [Fact]
    public void PermissionForAnUnseenCallShowsTheDiff()
    {
        using var doc = JsonDocument.Parse("""
            {"type":"permission.asked","properties":{"id":"per_1","sessionID":"ses_1","permission":"edit","patterns":["a.txt"],
             "metadata":{"filepath":"R:\\w\\a.txt","diff":"--- a\n+++ a\n@@ -1 +1 @@\n-old\n+new\n"},"always":["*"],
             "tool":{"messageID":"msg_1","callID":"call_1"}}}
            """);
        var translator = new OpenCodeTranslator("ses_1", Cwd);
        var events = translator.Handle(doc.RootElement).ToList();

        var call = Assert.Single(events.OfType<ToolCallEvent>());
        Assert.Equal("call_1", call.ToolUseId);
        var request = Assert.Single(events.OfType<PermissionRequestedEvent>()).Request;
        Assert.Equal("Edit", request.ToolName);
        Assert.Equal(@"R:\w\a.txt", request.Input.GetProperty("file_path").GetString());
        Assert.Equal("old", request.Input.GetProperty("old_string").GetString());
        Assert.Equal("new", request.Input.GetProperty("new_string").GetString());
    }

    [Fact]
    public void AbortedTurnIsInterruptedNotAnError()
    {
        var events = Replay("interrupt.jsonl", t => t.InterruptRequested = true);
        var done = Assert.Single(events.OfType<TurnCompletedEvent>()).Result;
        Assert.True(done.Interrupted);
        Assert.False(done.IsError);
        Assert.Null(done.ErrorMessage);

        // The aborted message's last part update comes after the turn ended: it must not add the text again.
        var end = events.FindIndex(e => e is TurnCompletedEvent);
        Assert.DoesNotContain(events.Skip(end), e => e is BlockStartedEvent or BlockCompletedEvent or TextDeltaEvent);
    }

    [Fact]
    public void AbortErrorAloneMarksTheTurnInterrupted()
    {
        // The Stop button may have been pressed in OpenCode's own app: its MessageAbortedError says so.
        var done = Assert.Single(Replay("interrupt.jsonl").OfType<TurnCompletedEvent>()).Result;
        Assert.True(done.Interrupted);
    }

    [Fact]
    public void OtherSessionsAreIgnored()
    {
        var translator = new OpenCodeTranslator("ses_other", Cwd);
        translator.BeginTurn();
        foreach (var line in File.ReadAllLines(Fixture("tool.jsonl")))
        {
            using var doc = JsonDocument.Parse(line);
            Assert.Empty(translator.Handle(doc.RootElement));
        }
    }

    [Fact]
    public void StoredMessagesBecomeHistory()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Fixture("tool.messages.json")));
        var history = OpenCodeHistory.FromMessages(doc.RootElement, "ses_x", null);

        var prompt = Assert.IsType<HistoryUserMessage>(history.Items[0]);
        Assert.StartsWith("Create a file hello.txt", prompt.Text);
        Assert.Equal(prompt.Uuid, prompt.ParentUuid);
        Assert.Equal("opencode/flint-40b-free", history.Model);
        Assert.Equal(Cwd, history.WorkingDirectory);
        Assert.Equal(["Write", "Write", "Read"], history.Items.OfType<HistoryToolCall>().Select(c => c.Name));
        Assert.Equal(3, history.Items.OfType<HistoryToolResult>().Count());
        Assert.StartsWith("It says **hi**", history.Items.OfType<HistoryAssistantText>().Last().Text);
    }

    [Fact]
    public void QuestionAnswersGoBackAsLabelsInOrder()
    {
        using var source = JsonDocument.Parse("""
            {"questions":[
              {"question":"Colour?","header":"Colour","options":[{"label":"Red","description":""},{"label":"Blue","description":""}]},
              {"question":"Snacks?","header":"Snacks","multiple":true,"options":[{"label":"Tea","description":""},{"label":"Cake","description":""}]}]}
            """);
        var input = OpenCodeTools.Questions(source.RootElement);
        Assert.True(input.GetProperty("questions")[1].GetProperty("multiSelect").GetBoolean());

        using var updated = JsonDocument.Parse("""{"answers":{"Colour?":"Blue","Snacks?":"Tea, Cake"}}""");
        var answers = OpenCodeTools.Answers(input, updated.RootElement);
        Assert.Equal(["Blue"], answers[0]);
        Assert.Equal(["Tea", "Cake"], answers[1]);
    }

    [Theory]
    [InlineData("openrouter/anthropic/claude-sonnet-4.6", "openrouter", "anthropic/claude-sonnet-4.6")]
    [InlineData("opencode/big-pickle", "opencode", "big-pickle")]
    public void ModelIdsSplitAtTheFirstSlash(string id, string provider, string model)
    {
        Assert.Equal((provider, model), OpenCodeClient.SplitModel(id));
        Assert.True(OpenCodeBackend.IsOpenCodeModel(id));
    }

    [Fact]
    public void ClaudeModelIdsAreNotOpenCodeOnes() => Assert.False(OpenCodeBackend.IsOpenCodeModel("claude-opus-5-5"));

    [Fact]
    public void PlaceholderTitlesReadAsNone()
    {
        Assert.Equal("", OpenCodeTitles.Clean("New session - 2026-10-29T09:03:44.589Z"));
        Assert.Equal("Create and read hello.txt", OpenCodeTitles.Clean("Create and read hello.txt"));
    }
}
