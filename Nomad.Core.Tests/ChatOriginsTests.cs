using Nomad.Core.Text;

namespace Nomad.Core.Tests;

public sealed class ChatOriginsTests
{
    [Fact]
    public void ReadsClaudeCodesCrossSessionMessage()
    {
        var origin = ChatOrigins.Parse(
            "<cross-session-message from=\"uds:\\\\.\\pipe\\LOCAL\\cc-msg-5b44\" from-name=\"Meitou Bug Fixing\" from-mode=\"bypass\">\n" +
            "Bug Fixing Claude: fine, go ahead.\nSecond line.\n</cross-session-message>");

        Assert.NotNull(origin);
        Assert.Equal("Meitou Bug Fixing", origin.Name);
        Assert.Equal("Claude Code", origin.Detail);
        Assert.Equal("Bug Fixing Claude: fine, go ahead.\nSecond line.", origin.Body);
    }

    [Fact]
    public void ReadsNomadsSendToChat()
    {
        var origin = ChatOrigins.Parse(
            "Message from chat \"Meitou Bug Fixing Gemini\" (Antigravity, id 9b19271f-3763-4f99-bf06-e05b4e6f5866), sent with Nomad's " +
            "send_to_chat. Reply with send_to_chat to that id only if a reply is asked for or needed, not to acknowledge.\n\nAll six points work for me.\n\nQuick heads-up.");

        Assert.NotNull(origin);
        Assert.Equal("Meitou Bug Fixing Gemini", origin.Name);
        Assert.Equal("Antigravity", origin.Detail);
        Assert.Equal("All six points work for me.\n\nQuick heads-up.", origin.Body);
    }

    [Theory]
    [InlineData("Fix the tests please")]
    [InlineData("What does <cross-session-message> mean?")]
    [InlineData("Message from chat about lunch")]
    [InlineData("")]
    public void LeavesTheUsersOwnWordsAlone(string text) => Assert.Null(ChatOrigins.Parse(text));
}
