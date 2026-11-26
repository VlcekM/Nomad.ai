using System.Text.Json;
using Nomad.Core.Agents;
using Nomad.Core.Hub;
using Nomad.Hub.Contracts;
using Nomad.Mobile.Hub;
using Nomad.Mobile.ViewModels;

namespace Nomad.Mobile.Tests;

/// <summary>The view models against a fake hub (docs/mobile.md §7): ordering, cards, sending during a turn, reconnecting.</summary>
[Collection("app services")]
public sealed class ViewModelTests
{
    public ViewModelTests() => AppServices.Post = work => work();

    private static async Task<(MainViewModel Main, FakeHub Hub, MemorySecretStore Store)> SignedInAsync()
    {
        var hub = new FakeHub();
        hub.Board.AddRange(Samples.Board());
        var store = new MemorySecretStore();
        new Enrollment(new Uri("https://hub.example.ts.net/"), Guid.NewGuid(), "Test phone", "secret").Save(store);
        var main = new MainViewModel(store, hub);
        main.Start();
        await Task.Yield();
        return (main, hub, store);
    }

    private static async Task<ChatViewModel> OpenAsync(MainViewModel main, string sessionId)
    {
        var row = main.Chats.Groups.SelectMany(g => g.Chats).Single(c => c.SessionId == sessionId);
        main.OpenChatRow(row);
        var chat = Assert.IsType<ChatViewModel>(main.Page);
        await chat.AttachAsync();
        return chat;
    }

    [Fact]
    public async Task Chats_wait_first_then_working_then_newest()
    {
        var (main, _, _) = await SignedInAsync();

        Assert.True(main.IsConnected);
        Assert.Null(main.Chats.Status);
        // The PC with something waiting comes first; within it, waiting, working, then by last activity.
        Assert.Equal(["Desk PC", "Laptop"], main.Chats.Groups.Select(g => g.Name));
        Assert.Equal(["d3", "d2", "d1", "d4"], main.Chats.Groups[0].Chats.Select(c => c.SessionId));
        Assert.Equal("1 waiting on you", main.Chats.Groups[0].Summary);
        var waiting = main.Chats.Groups[0].Chats[0];
        Assert.Equal("Waiting: allow Bash", waiting.StateText);
        Assert.Equal("notes", waiting.Folder);
        Assert.Equal("Nomad.ai", main.Chats.Groups[0].Chats[1].Folder); // A Windows path, read on a phone.
    }

    [Fact]
    public async Task Board_keeps_the_newest_update_and_drops_a_PC_that_left()
    {
        var (main, hub, _) = await SignedInAsync();
        var desk = main.Chats.Groups[0];
        var row = desk.Chats.Single(c => c.SessionId == "d1");

        hub.RaiseBoard(new BoardDevice(Samples.Desk, "Desk PC", [Samples.Chat("d1", "Fix the parser bug", "waiting", 0, waiting: "a question")], 8));
        Assert.Same(desk, main.Chats.Groups[0]); // Kept and updated, not rebuilt.
        Assert.Same(row, desk.Chats.Single());
        Assert.True(row.IsWaiting);

        hub.RaiseBoard(new BoardDevice(Samples.Desk, "Desk PC", [Samples.Chat("d9", "Stale", "idle", 0)], 5)); // Older: ignored.
        Assert.Equal("d1", desk.Chats.Single().SessionId);

        hub.RaiseBoard(new BoardDevice(Samples.Laptop, "Laptop", [], 9));
        Assert.Equal(["Desk PC"], main.Chats.Groups.Select(g => g.Name));

        hub.RaiseBoard(new BoardDevice(Samples.Desk, "Desk PC", [], 10));
        Assert.Empty(main.Chats.Groups);
        Assert.Contains("Stream live sessions", main.Chats.Status);
    }

    [Fact]
    public async Task A_permission_card_is_answered_with_its_id()
    {
        var (main, hub, _) = await SignedInAsync();
        hub.Pages["d3"] = Samples.Page(withCard: true);
        hub.Answer = input => new ChatInputResult(true, "Answered.");
        var chat = await OpenAsync(main, "d3");

        var card = Assert.IsType<CardViewModel>(chat.Card);
        Assert.Equal("Allow Bash?", card.Title);
        Assert.True(card.CanAllowForSession);
        await card.AllowForSessionCommand.ExecuteAsync(null);

        var sent = Assert.Single(hub.Sent);
        Assert.Equal((Samples.Desk, "claude-code", "d3"), (sent.Device, sent.Backend, sent.Session));
        Assert.Equal(("answer", "p-1", "session"), (sent.Input.Kind, sent.Input.Card, sent.Input.Choice));
        Assert.False(string.IsNullOrEmpty(sent.Input.Id)); // A retry is applied once.
        Assert.Equal("Answered.", chat.Status);
    }

    [Fact]
    public async Task A_question_is_sent_only_once_every_question_has_an_answer()
    {
        var (main, hub, _) = await SignedInAsync();
        hub.Pages["d3"] = Samples.Page(withCard: true, question: true);
        var chat = await OpenAsync(main, "d3");
        var card = Assert.IsType<CardViewModel>(chat.Card);
        var question = Assert.Single(card.Questions);

        Assert.False(card.SubmitCommand.CanExecute(null));
        question.Options[0].IsSelected = true;
        question.Options[1].IsSelected = true; // One choice only: picking another clears the first.
        Assert.False(question.Options[0].IsSelected);
        Assert.True(card.SubmitCommand.CanExecute(null));
        question.Other = "  and add a test  ";
        await card.SubmitCommand.ExecuteAsync(null);

        var input = Assert.Single(hub.Sent).Input;
        Assert.Equal(("answer", "q-1", "allow"), (input.Kind, input.Card, input.Choice));
        var answer = Assert.Single(input.Answers!);
        Assert.Equal(["Parser"], answer.Options);
        Assert.Equal("and add a test", answer.Other);
    }

    [Fact]
    public async Task A_message_sent_during_a_turn_says_it_was_queued()
    {
        var (main, hub, _) = await SignedInAsync();
        hub.Pages["d2"] = Samples.Page(state: "working");
        hub.Answer = input => input.Kind == "prompt"
            ? new ChatInputResult(true, "Queued: it is working, and gets this when the turn ends.")
            : new ChatInputResult(true, "Stopping.");
        var chat = await OpenAsync(main, "d2");

        Assert.True(chat.IsWorking);
        Assert.True(chat.StopCommand.CanExecute(null));
        Assert.False(chat.SendCommand.CanExecute(null)); // Nothing typed yet.
        chat.Draft = "  Also check the Linux build.  ";
        await chat.SendCommand.ExecuteAsync(null);

        var input = Assert.Single(hub.Sent).Input;
        Assert.Equal(("prompt", "Also check the Linux build."), (input.Kind, input.Text));
        Assert.Equal("", chat.Draft);
        Assert.StartsWith("Queued", chat.Status);
        Assert.False(chat.StatusIsProblem);

        await chat.StopCommand.ExecuteAsync(null);
        Assert.Equal("stop", hub.Sent[^1].Input.Kind);
    }

    [Fact]
    public async Task A_refused_message_is_kept_and_shown_as_a_problem()
    {
        var (main, hub, _) = await SignedInAsync();
        hub.Answer = _ => new ChatInputResult(false, "That chat is no longer open in Nomad.");
        var chat = await OpenAsync(main, "d1");
        chat.Draft = "Hello";
        await chat.SendCommand.ExecuteAsync(null);

        Assert.Equal("Hello", chat.Draft);
        Assert.True(chat.StatusIsProblem);
        Assert.Equal("That chat is no longer open in Nomad.", chat.Status);
    }

    [Fact]
    public async Task Reconnecting_fetches_the_board_and_the_open_chat_again()
    {
        var (main, hub, _) = await SignedInAsync();
        hub.Pages["d1"] = Samples.Page();
        var chat = await OpenAsync(main, "d1");
        hub.Calls.Clear();

        hub.SetConnected(false);
        Assert.False(main.IsConnected);
        Assert.Contains("Reconnecting", main.Chats.Status);
        hub.SetConnected(true);
        await Task.Yield();

        Assert.True(main.IsConnected);
        Assert.Contains("BoardWatch", hub.Calls);
        Assert.Contains("Watch d1", hub.Calls); // The turn so far comes with it.
        Assert.Contains("History d1 newest", hub.Calls);
        Assert.Equal(5, chat.Entries.Count);
    }

    [Fact]
    public async Task Coming_back_from_the_background_reconnects_a_closed_connection()
    {
        var (main, hub, _) = await SignedInAsync();
        hub.SetConnected(false);
        hub.Calls.Clear();

        main.OnResumed();
        await Task.Yield();

        Assert.Contains("Connect", hub.Calls);
        Assert.Contains("BoardWatch", hub.Calls);
        Assert.True(main.IsConnected);
    }

    [Fact]
    public async Task The_turn_streams_into_the_last_entry_and_skips_events_it_already_has()
    {
        var (main, hub, _) = await SignedInAsync();
        hub.Pages["d1"] = Samples.Page(state: "working");
        hub.Watch = new WatchResult(true, "Desk PC", [Encode(new TurnStartedEvent("Next"))], 1);
        var chat = await OpenAsync(main, "d1");
        Assert.True(chat.IsLive);
        Assert.Equal("working", chat.State);

        hub.RaiseRelay("claude-code", "d1", 0, Encode(new TurnStartedEvent("Next"))); // Already had it from the buffer.
        hub.RaiseRelay("claude-code", "d1", 1, Encode(new AssistantMessageStartedEvent("m1", null, null)), Encode(new TextDeltaEvent("m1", 0, "Hel", null)));
        hub.RaiseRelay("claude-code", "d1", 3, Encode(new TextDeltaEvent("m1", 0, "lo", null)), Encode(new TextDeltaEvent("sub", 0, "subagent text", "tool-1")));

        var last = chat.Entries[^1];
        Assert.True(last.IsAssistant);
        Assert.True(last.IsLocal);
        Assert.Equal("Hello", last.Text);
        Assert.Equal(6, chat.Entries.Count);

        hub.RaiseRelay("claude-code", "d1", 5, Encode(new ToolCallEvent("t1", "Bash", JsonDocument.Parse("{}").RootElement.Clone(), null)));
        Assert.Equal("Using Bash", chat.Activity);
    }

    [Fact]
    public async Task Older_pages_load_before_the_first_entry_and_a_changed_transcript_starts_again()
    {
        var (main, hub, _) = await SignedInAsync();
        var many = Enumerable.Range(0, 100).Select(i => new ChatEntry(i, i % 2 == 0 ? "user" : "assistant", $"Message {i}")).ToList();
        hub.Pages["d1"] = new ChatPage("idle", 0, 100, many, Generation: 3);
        var chat = await OpenAsync(main, "d1");

        Assert.Equal(ChatViewModel.PageSize, chat.Entries.Count);
        Assert.Equal(60, chat.Entries[0].Index);
        Assert.True(chat.HasOlder);
        await chat.LoadOlderCommand.ExecuteAsync(null);
        Assert.Equal(20, chat.Entries[0].Index);
        Assert.Equal(80, chat.Entries.Count);

        hub.Pages["d1"] = new ChatPage("idle", 0, 100, many, Generation: 4); // Rewound on the PC: positions no longer fit.
        await chat.LoadOlderCommand.ExecuteAsync(null);
        Assert.Equal(60, chat.Entries[0].Index);
        Assert.Equal(ChatViewModel.PageSize, chat.Entries.Count);
    }

    [Fact]
    public async Task Connecting_enrolls_saves_and_shows_the_chats()
    {
        var hub = new FakeHub();
        hub.Board.AddRange(Samples.Board());
        var store = new MemorySecretStore();
        var main = new MainViewModel(store, hub);
        main.Start();
        Assert.IsType<ConnectViewModel>(main.Page);

        main.HandleLink("nomad://enroll?hub=https%3A%2F%2Fdesk.example.ts.net&token=abc123");
        Assert.Equal("https://desk.example.ts.net", main.Connect.Address);
        Assert.Equal("abc123", main.Connect.Token);
        main.Connect.DeviceName = "Test phone";
        await main.Connect.ConnectCommand.ExecuteAsync(null);

        Assert.IsType<ChatsViewModel>(main.Page);
        Assert.Equal("abc123", hub.EnrolledToken);
        var saved = Enrollment.Load(store)!;
        Assert.Equal((hub.EnrolledId, "Test phone", "secret-abc123"), (saved.DeviceId, saved.DeviceName, saved.Secret));
        Assert.Equal(2, main.Chats.Groups.Count);
    }

    [Fact]
    public async Task Connect_refuses_plain_http_and_explains_a_refused_token()
    {
        var hub = new FakeHub();
        var main = new MainViewModel(new MemorySecretStore(), hub);
        main.Start();

        main.Connect.Fill("http://192.168.1.5:8080", "abc");
        await main.Connect.ConnectCommand.ExecuteAsync(null);
        Assert.Contains("HTTPS", main.Connect.Error);
        Assert.Null(hub.EnrolledToken);

        main.Connect.Fill("desk.example.ts.net", "bad");
        await main.Connect.ConnectCommand.ExecuteAsync(null);
        Assert.Contains("refused the token", main.Connect.Error);
        Assert.IsType<ConnectViewModel>(main.Page);
    }

    [Fact]
    public async Task Signing_out_revokes_this_phone_and_forgets_it()
    {
        var (main, hub, store) = await SignedInAsync();
        var id = main.Enrollment!.DeviceId;
        var settings = new SettingsViewModel(main);

        await settings.SignOutCommand.ExecuteAsync(null);
        Assert.True(settings.ConfirmingSignOut); // Asks once more first.
        Assert.Empty(hub.Revoked);
        await settings.SignOutCommand.ExecuteAsync(null);

        Assert.Equal([id], hub.Revoked);
        Assert.Null(Enrollment.Load(store));
        Assert.IsType<ConnectViewModel>(main.Page);
        Assert.Empty(main.Chats.Groups);
    }

    [Fact]
    public async Task Back_goes_from_a_chat_to_the_chats_and_stops_watching()
    {
        var (main, hub, _) = await SignedInAsync();
        await OpenAsync(main, "d1");

        Assert.True(main.GoBack());
        Assert.IsType<ChatsViewModel>(main.Page);
        Assert.Contains("Unwatch d1", hub.Calls);
        Assert.False(main.GoBack()); // From the chats, back leaves the app.
    }

    [Theory]
    [InlineData("nomad://enroll?hub=https%3A%2F%2Fa.ts.net%3A8443&token=t1", "https://a.ts.net:8443", "t1")]
    [InlineData("NOMAD://ENROLL?token=t2&hub=https://b.example", "https://b.example", "t2")]
    [InlineData("nomad://enroll?hub=&token=t3", null, null)]
    [InlineData("https://a.ts.net", null, null)]
    [InlineData("nomad://other?hub=x&token=y", null, null)]
    public void Enroll_links_parse(string link, string? hub, string? token)
    {
        var parsed = EnrollLink.TryParse(link);
        Assert.Equal(hub, parsed?.Hub);
        Assert.Equal(token, parsed?.Token);
    }

    [Theory]
    [InlineData("https://desk.example.ts.net/", "kuxZR6HOMWtNIVf1GN069W1w")]
    [InlineData("https://hub.example.com:8443/nomad?x=1&y=2", "a+b/c=d")]
    public void The_link_Add_phone_makes_reads_back_on_the_phone(string hub, string token)
    {
        var parsed = EnrollLink.TryParse(Nomad.Core.Hub.PhoneEnrollLink.Make(hub, token));
        Assert.Equal((hub, token), parsed);
    }

    [Theory]
    [InlineData("desk.example.ts.net", "https://desk.example.ts.net/")]
    [InlineData("https://desk.example.ts.net:8443/", "https://desk.example.ts.net:8443/")]
    [InlineData("http://desk.example.ts.net", null)]
    [InlineData("", null)]
    public void Hub_addresses_must_be_https(string address, string? url) =>
        Assert.Equal(url, EnrollLink.CheckAddress(address).Url?.ToString());

    private static string Encode(AgentEvent e) => RelayCodec.Encode(e) ?? throw new InvalidOperationException($"{e.GetType().Name} isn't relayed.");
}
