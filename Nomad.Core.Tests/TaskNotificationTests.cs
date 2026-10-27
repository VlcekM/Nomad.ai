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

    [Theory]
    [InlineData("Fix the <task-notification> parser")]
    [InlineData("Run the tests")]
    [InlineData(null)]
    public void APromptIsNotOne(string? text) => Assert.False(TaskNotification.TryParse(text, out _));
}
