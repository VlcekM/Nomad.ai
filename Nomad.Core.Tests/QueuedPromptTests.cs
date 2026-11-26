using Nomad.Core.Sessions;

namespace Nomad.Core.Tests;

/// <summary>A prompt sent into a running turn, which Claude Code stores as a queued_command attachment, not a user record.</summary>
public class QueuedPromptTests
{
    // As Claude Code 2.1.310 wrote them: the turn's prompt, a tool round, then the prompt sent mid-turn (with the reminder
    // Nomad adds to every prompt after the first, which is not shown).
    private static readonly string[] Lines =
    [
        """{"type":"user","uuid":"u1","parentUuid":null,"sessionId":"s","timestamp":"2026-11-21T10:00:00.000Z","cwd":"R:\\Work","message":{"role":"user","content":[{"type":"text","text":"Run three commands."}]}}""",
        """{"type":"assistant","uuid":"a1","parentUuid":"u1","sessionId":"s","timestamp":"2026-11-21T10:00:01.000Z","message":{"id":"m1","role":"assistant","content":[{"type":"tool_use","id":"t1","name":"Bash","input":{"command":"sleep 8"}}]}}""",
        """{"type":"user","uuid":"r1","parentUuid":"a1","sessionId":"s","timestamp":"2026-11-21T10:00:09.000Z","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"t1","content":"ok"}]}}""",
        """{"parentUuid":"r1","isSidechain":false,"attachment":{"type":"queued_command","prompt":[{"type":"text","text":"Also add the word PICKLE to your final reply."},{"type":"text","text":"<system-reminder>A reminder Nomad adds.</system-reminder>"},{"type":"image","source":{"type":"base64","media_type":"image/png","data":"iVBORw0KGgo="}}],"source_uuid":"2b146e84-b169-4809-b625-76713e4fdd26"},"type":"attachment","uuid":"q1","timestamp":"2026-11-21T10:00:10.000Z","sessionId":"s"}""",
        """{"type":"assistant","uuid":"a2","parentUuid":"q1","sessionId":"s","timestamp":"2026-11-21T10:00:20.000Z","message":{"id":"m2","role":"assistant","content":[{"type":"text","text":"FINISHED PICKLE"}]}}""",
    ];

    [Fact]
    public void APromptTakenInMidTurnShowsAsAPromptThatIsNoBranchPoint()
    {
        var history = SessionHistory.FromRecords([.. Lines.Select(l => TranscriptRecord.Parse(l)!)], truncated: false, "s");

        var prompts = history.Items.OfType<HistoryUserMessage>().ToList();
        Assert.Equal(["Run three commands.", "Also add the word PICKLE to your final reply."], prompts.Select(p => p.Text));
        Assert.False(prompts[0].IsQueued);
        Assert.True(prompts[1].IsQueued);
        Assert.Null(prompts[1].Uuid); // Not a record a rewind or fork can resume at.
        Assert.Equal("image/png", Assert.Single(prompts[1].Images).MediaType);
    }

    [Fact]
    public void AQueuedCommandWithATextPromptIsNoPrompt()
    {
        var notification = """{"parentUuid":"a2","isSidechain":false,"attachment":{"type":"queued_command","prompt":"<task-notification>\n<task-id>b1</task-id>\n<tool-use-id>t9</tool-use-id>\n<status>completed</status>\n<summary>Background command \"build\" completed</summary>\n</task-notification>"},"type":"attachment","uuid":"q2","timestamp":"2026-11-21T10:01:00.000Z","sessionId":"s"}""";
        var history = SessionHistory.FromRecords([.. Lines.Take(3).Append(notification.Replace("\"parentUuid\":\"a2\"", "\"parentUuid\":\"r1\"")).Append("""{"type":"assistant","uuid":"a4","parentUuid":"q2","sessionId":"s","timestamp":"2026-11-21T10:03:00.000Z","message":{"id":"ma4","role":"assistant","content":[{"type":"text","text":"Done."}]}}""").Select(l => TranscriptRecord.Parse(l)!)], truncated: false, "s");

        Assert.Single(history.Items.OfType<HistoryUserMessage>()); // Only the turn's own prompt: the notification is a notice, as before.
        Assert.Contains(history.Items, i => i is HistoryNotice);
    }

    [Fact]
    public void WhatHooksAddForANotificationFollowsItsNoticeNotThePrompt()
    {
        // Claude Code runs the prompt hooks for a task notification too; its hook context comes after the notification.
        var hook = """{"parentUuid":"u1","isSidechain":false,"attachment":{"type":"hook_additional_context","content":["Message sent: 10:00"],"hookName":"UserPromptSubmit"},"type":"attachment","uuid":"h1","timestamp":"2026-11-21T10:00:00.500Z","sessionId":"s"}""";
        var notification = """{"parentUuid":"r1","isSidechain":false,"attachment":{"type":"queued_command","prompt":"<task-notification>\n<task-id>b1</task-id>\n<tool-use-id>t9</tool-use-id>\n<status>completed</status>\n<summary>Background command \"build\" completed</summary>\n</task-notification>"},"type":"attachment","uuid":"q2","timestamp":"2026-11-21T10:01:00.000Z","sessionId":"s"}""";
        var notificationHook = hook.Replace("\"uuid\":\"h1\"", "\"uuid\":\"h2\"").Replace("\"parentUuid\":\"u1\"", "\"parentUuid\":\"q2\"")
            .Replace("Message sent: 10:00", "Background notice at: 10:01").Replace("10:00:00.500Z", "10:01:00.500Z");

        // One chain, as the CLI writes it: prompt, its hook context, the tool round, the notification, its hook context, the reply.
        string[] lines = [Lines[0], hook, Lines[1].Replace("\"parentUuid\":\"u1\"", "\"parentUuid\":\"h1\""), Lines[2], notification, notificationHook,
            """{"type":"assistant","uuid":"a4","parentUuid":"h2","sessionId":"s","timestamp":"2026-11-21T10:01:05.000Z","message":{"id":"ma4","role":"assistant","content":[{"type":"text","text":"The build finished."}]}}"""];
        var history = SessionHistory.FromRecords([.. lines.Select(l => TranscriptRecord.Parse(l)!)], truncated: false, "s");

        var order = history.Items.Where(i => i is HistoryUserMessage or HistoryHookContext or HistoryNotice).ToList();
        Assert.IsType<HistoryUserMessage>(order[0]);
        Assert.Equal("Message sent: 10:00", Assert.IsType<HistoryHookContext>(order[1]).Text);
        Assert.True(Assert.IsType<HistoryNotice>(order[2]).IsTaskNotification); // So the view keeps the next one off the prompt.
        Assert.Equal("Background notice at: 10:01", Assert.IsType<HistoryHookContext>(order[3]).Text);
    }

    [Fact]
    public void APromptTypedMidTurnInATerminalShowsToo()
    {
        var typed = """{"parentUuid":"r1","isSidechain":false,"attachment":{"type":"queued_command","prompt":"<div> isn't centred, fix it"},"type":"attachment","uuid":"q3","timestamp":"2026-11-21T10:02:00.000Z","sessionId":"s"}""";
        var history = SessionHistory.FromRecords([.. Lines.Take(3).Append(typed).Append("""{"type":"assistant","uuid":"a3","parentUuid":"q3","sessionId":"s","timestamp":"2026-11-21T10:03:00.000Z","message":{"id":"ma3","role":"assistant","content":[{"type":"text","text":"Done."}]}}""").Select(l => TranscriptRecord.Parse(l)!)], truncated: false, "s");

        var prompt = history.Items.OfType<HistoryUserMessage>().Last();
        Assert.Equal("<div> isn't centred, fix it", prompt.Text); // Only Nomad's reminder is left out, not any text starting with '<'.
        Assert.True(prompt.IsQueued);
        Assert.Equal(new DateTime(2026, 11, 21, 10, 2, 0, DateTimeKind.Utc), prompt.TimestampUtc);
    }
}
