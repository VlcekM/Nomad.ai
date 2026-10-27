using Nomad.Core.Sessions;

namespace Nomad.Core.Tests;

public class TaskNotificationTests
{
    [Fact]
    public void ANotificationGivesItsSummary()
    {
        const string text = "<task-notification>\n<task-id>a0aa</task-id>\n<status>completed</status>\n<summary>Agent \"Find it\" finished</summary>\n<result>Long report…</result>\n</task-notification>";

        Assert.True(TaskNotification.TryParse(text, out var summary));
        Assert.Equal("Agent \"Find it\" finished", summary);
    }

    [Fact]
    public void ABackgroundCommandsNotificationGivesItsIdsAndStatus()
    {
        const string text = "<task-notification>\n<task-id>b7k2p9</task-id>\n<tool-use-id>toolu_01AB</tool-use-id>\n<status>Failed</status>\n<summary>Background command \"Build\" failed with exit code 1</summary>\n</task-notification>";

        Assert.Equal("b7k2p9", TaskNotification.TaskId(text));
        Assert.Equal("toolu_01AB", TaskNotification.ToolUseId(text));
        Assert.Equal("failed", TaskNotification.Status(text));
    }

    [Fact]
    public void ALaunchedCommandNamesItsTask() =>
        Assert.Equal("b7k2p9", TaskNotification.LaunchedTaskId("Command running in background with ID: b7k2p9. Output is being written to: C:\\Temp\\b7k2p9.output"));

    [Theory]
    [InlineData("Fix the <task-notification> parser")]
    [InlineData("Run the tests")]
    [InlineData(null)]
    public void APromptIsNotOne(string? text) => Assert.False(TaskNotification.TryParse(text, out _));
}
