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
        // PCs by name; within one, at first sight: waiting, working, then by last activity.
        Assert.Equal(["Desk PC", "Laptop"], main.Chats.Groups.Select(g => g.Name));
        Assert.Equal(["d3", "d2", "d1", "d4"], main.Chats.Groups[0].Chats.Select(c => c.SessionId));
        Assert.Equal("1 waiting on you", main.Chats.Groups[0].Summary);
        var waiting = main.Chats.Groups[0].Chats[0];
        Assert.Equal("Waiting: allow Bash", waiting.StateText);
        Assert.Equal("notes", waiting.Folder);
        Assert.Equal("Nomad.ai", main.Chats.Groups[0].Chats[1].Folder); // A Windows path, read on a phone.
    }

    [Fact]
    public async Task Chats_keep_their_place_as_they_work_and_new_ones_go_last()
    {
        var (main, hub, _) = await SignedInAsync();
        var desk = main.Chats.Groups[0];
        var before = desk.Chats.Select(c => c.SessionId).ToList();

        // The last chat starts working and the rest go idle: nothing moves under the finger.
        hub.RaiseBoard(new BoardDevice(Samples.Desk, "Desk PC",
            [.. before.Select((id, n) => Samples.Chat(id, "Chat " + id, n == before.Count - 1 ? "working" : "idle", n))], 20));
        Assert.Equal(before, desk.Chats.Select(c => c.SessionId));

        // A new chat goes at the end, though it is the newest and waiting.
        hub.RaiseBoard(new BoardDevice(Samples.Desk, "Desk PC",
            [Samples.Chat("d9", "New", "waiting", 0, waiting: "a question"), .. before.Select((id, n) => Samples.Chat(id, "Chat " + id, "idle", n + 1))], 21));
        Assert.Equal([.. before, "d9"], desk.Chats.Select(c => c.SessionId));
    }

    [Fact]
    public async Task Chat_settings_show_the_PCs_choices_and_send_a_pick()
    {
        var (main, hub, _) = await SignedInAsync();
        var chat = await OpenAsync(main, "d1");

        chat.OpenSettingsCommand.Execute(null);
        var panel = Assert.IsType<ChatSettingsViewModel>(chat.SettingsPanel);
        await panel.LoadAsync();
        Assert.Equal("opus", panel.Model.Selected?.Id);
        Assert.Equal("High", panel.Effort.ValueText);
        Assert.Equal(["Manual", "Plan"], panel.Mode.Choices.Select(c => c.Name));

        // A pick goes to the PC as that chat's input, and what shows after is what the PC then says.
        panel.Model.ToggleCommand.Execute(null);
        Assert.True(panel.Model.IsOpen);
        hub.Settings = hub.Settings with { Model = "sonnet" };
        panel.Model.Choices.Single(c => c.Id == "sonnet").PickCommand.Execute(null);
        await Task.Yield();
        var sent = hub.Sent.Last();
        Assert.Equal(("d1", "model", "sonnet"), (sent.Session, sent.Input.Kind, sent.Input.Choice));
        Assert.False(panel.Model.IsOpen);
        Assert.Equal("sonnet", panel.Model.Selected?.Id);

        // Back closes the settings first, then the chat.
        Assert.True(main.GoBack());
        Assert.Null(chat.SettingsPanel);
        Assert.Same(chat, main.Page);
    }

    [Fact]
    public async Task New_chat_offers_the_PCs_agents_and_folders_and_starts_with_the_picks()
    {
        var (main, hub, _) = await SignedInAsync();

        main.Chats.NewChatCommand.Execute(null);
        var page = Assert.IsType<NewChatViewModel>(main.Page);
        await page.LoadAsync();
        Assert.Equal("Desk PC", page.Pc.Selected?.Name); // PCs by name.
        Assert.Equal("Claude Code", page.Agent.Selected?.Name); // The PC's default agent.
        Assert.Equal(["Nomad.ai", "Other", "No folder"], page.Folder.Choices.Select(c => c.Name));
        Assert.Equal("Nomad.ai", page.Folder.Selected?.Name);
        Assert.Equal("opus", page.Model.Selected?.Id);
        Assert.False(page.StartCommand.CanExecute(null)); // No first message yet.

        // Another agent brings its own models and modes; Codex here has a single model, so its row hides.
        page.Agent.Choices.Single(c => c.Id == "codex").PickCommand.Execute(null);
        Assert.Equal(["GPT"], page.Model.Choices.Select(c => c.Name));
        Assert.False(page.Model.HasChoices);
        page.Agent.Choices.Single(c => c.Id == "claude-code").PickCommand.Execute(null);
        page.Model.Choices.Single(c => c.Id == "sonnet").PickCommand.Execute(null);
        page.Folder.Choices.Single(c => c.Name == "No folder").PickCommand.Execute(null);

        // Started, it opens once the PC has put it on the board.
        hub.RaiseBoard(new BoardDevice(Samples.Desk, "Desk PC", [Samples.Chat("new-1", "A new chat", "working", 0)], 20));
        page.Prompt = "Look at the parser.";
        await page.StartCommand.ExecuteAsync(null);
        var (device, started) = Assert.Single(hub.Started);
        Assert.Equal(Samples.Desk, device);
        Assert.Equal(("claude-code", null, "sonnet", "high", "default", "Look at the parser."),
            (started.Backend, started.Folder, started.Model, started.Effort, started.Mode, started.Prompt));
        Assert.Equal("new-1", Assert.IsType<ChatViewModel>(main.Page).SessionId);
    }

    [Fact]
    public async Task A_PC_with_no_chats_open_stays_listed_while_online_and_leaves_when_gone()
    {
        var (main, hub, _) = await SignedInAsync();

        hub.RaiseBoard(new BoardDevice(Samples.Laptop, "Laptop", [], 30, Online: true));
        Assert.Equal("No open chats", main.Chats.Groups.Single(g => g.Name == "Laptop").Summary);
        Assert.Contains(main.Chats.Devices, d => d.DeviceName == "Laptop"); // A new chat can start there.

        hub.RaiseBoard(new BoardDevice(Samples.Laptop, "Laptop", [], 31));
        Assert.DoesNotContain(main.Chats.Groups, g => g.Name == "Laptop");
    }

    [Fact]
    public async Task Connecting_to_another_hub_drops_the_old_hubs_PCs()
    {
        var (main, hub, _) = await SignedInAsync();
        Assert.Equal(2, main.Chats.Groups.Count);

        // The new hub has seen fewer changes, so its sequence numbers are lower: the old PCs must still go.
        hub.Board.Clear();
        hub.Board.Add(new BoardDevice(Guid.NewGuid(), "Test PC", [Samples.Chat("t1", "Test chat", "idle", 1)], 2, Online: true));
        await main.EnrollAsync(new Uri("http://10.0.2.2:8096/"), "token", "Test phone");

        Assert.Equal(["Test PC"], main.Chats.Groups.Select(g => g.Name));
    }

    [Fact]
    public async Task A_new_chat_the_PC_refuses_stays_with_its_reason()
    {
        var (main, hub, _) = await SignedInAsync();
        hub.StartAnswer = _ => new NewChatResult(false, "That folder isn't on this PC.");

        main.Chats.NewChatCommand.Execute(null);
        var page = Assert.IsType<NewChatViewModel>(main.Page);
        await page.LoadAsync();
        page.Prompt = "Hello";
        await page.StartCommand.ExecuteAsync(null);

        Assert.Same(page, main.Page);
        Assert.Equal("That folder isn't on this PC.", page.Status);
        Assert.True(page.StatusIsProblem);
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

        // Typing an answer of one's own replaces the option, and picking one again clears what was typed: the PC takes one.
        question.Other = "Both, with a test";
        Assert.False(question.Options[1].IsSelected);
        question.Options[1].IsSelected = true;
        Assert.Equal("", question.Other);
        await card.SubmitCommand.ExecuteAsync(null);

        var input = Assert.Single(hub.Sent).Input;
        Assert.Equal(("answer", "q-1", "allow"), (input.Kind, input.Card, input.Choice));
        var answer = Assert.Single(input.Answers!);
        Assert.Equal(["Parser"], answer.Options);
        Assert.Null(answer.Other);
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

        Assert.Contains("ReconnectNow", hub.Calls); // At once, not after the retry's wait.
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
        Assert.Equal("Using Bash", chat.Activity); // No input to show.
    }

    [Fact]
    public async Task An_activity_opens_to_show_each_command_and_thought()
    {
        var (main, hub, _) = await SignedInAsync();
        hub.Pages["d1"] = Samples.Page() with { Entries = [.. Samples.Page().Entries, new ChatEntry(5, "thinking", "First, check the lexer.\nThen the parser.")] };
        var chat = await OpenAsync(main, "d1");

        var activity = chat.Entries[1];
        Assert.True(activity.HasSteps);
        Assert.Equal(["thinking", "Read", "Grep", "Bash"], activity.Steps.Select(s => s.IsThought ? "thinking" : s.Title));
        Assert.True(activity.Steps[^1].Failed);
        Assert.False(activity.IsOpen);
        activity.ToggleOpenCommand.Execute(null);
        Assert.True(activity.IsOpen);

        var thought = chat.Entries[^1];
        Assert.True(thought.IsThinking);
        Assert.False(new EntryViewModel(new ChatEntry(9, "thinking", new string('x', 3000))).IsClipped); // A thought folds by itself.
        Assert.Equal("First, check the lexer. …", thought.ThinkingText);
        thought.ToggleOpenCommand.Execute(null);
        Assert.Equal("First, check the lexer.\nThen the parser.", thought.ThinkingText);

        // The agent then calls a tool, and the thought moves into a new activity in the same place: it starts folded.
        thought.Update(new ChatEntry(thought.Index, "activity", "Read Lexer.cs", Steps: [new ChatStep("tool", "Read", "Lexer.cs")]));
        Assert.False(thought.IsOpen);
    }

    [Fact]
    public void The_live_line_says_what_is_running()
    {
        var input = JsonDocument.Parse("{\"command\":\"dotnet test\\n  --no-build\",\"timeout\":60}").RootElement.Clone();
        Assert.Equal("Bash: dotnet test   --no-build", ChatViewModel.ActivityLine(new ToolCallEvent("t", "Bash", input, null)));
        var empty = JsonDocument.Parse("{}").RootElement.Clone();
        Assert.Equal("Using Agent", ChatViewModel.ActivityLine(new ToolCallEvent("t", "Agent", empty, null)));
        var blank = JsonDocument.Parse("{\"description\":\"   \",\"prompt\":\"Look\"}").RootElement.Clone();
        Assert.Equal("Agent: Look", ChatViewModel.ActivityLine(new ToolCallEvent("t", "Agent", blank, null)));
        var emoji = new string('a', 119) + "\U0001F600tail";
        var withEmoji = JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, string> { ["command"] = emoji })).RootElement.Clone();
        Assert.Equal("Bash: " + new string('a', 119) + "…", ChatViewModel.ActivityLine(new ToolCallEvent("t", "Bash", withEmoji, null)));
    }

    [Fact]
    public async Task A_stream_that_ends_and_starts_again_is_followed_from_its_first_event()
    {
        var (main, hub, _) = await SignedInAsync();
        hub.Pages["d1"] = Samples.Page(state: "working");
        var chat = await OpenAsync(main, "d1");
        hub.RaiseRelay("claude-code", "d1", 0, Encode(new TurnStartedEvent("One")), Encode(new TextDeltaEvent("m1", 0, "First turn", null)));
        Assert.Equal("First turn", chat.Entries[^1].Text);

        // The chat's process exits, and a new one starts streaming, numbered from 0 again.
        hub.RaiseRelayEnded("claude-code", "d1");
        Assert.False(chat.IsLive);
        hub.RaiseRelay("claude-code", "d1", 0, Encode(new AssistantMessageStartedEvent("m2", null, null)), Encode(new TextDeltaEvent("m2", 0, "Second", null)));
        Assert.True(chat.IsLive);
        Assert.Equal("Second", chat.Entries[^1].Text);

        // Even without the end being seen (the phone was reconnecting), a stream starting from 0 is a new one.
        hub.RaiseRelay("claude-code", "d1", 0, Encode(new AssistantMessageStartedEvent("m3", null, null)), Encode(new TextDeltaEvent("m3", 0, "Third", null)));
        Assert.Equal("Third", chat.Entries[^1].Text);
    }

    [Fact]
    public async Task A_chat_that_isnt_streamed_fetches_its_page_when_the_board_says_it_changed()
    {
        var (main, hub, _) = await SignedInAsync();
        hub.Watch = new WatchResult(false, null, [], 0);
        hub.Pages["d1"] = Samples.Page();
        var chat = await OpenAsync(main, "d1");
        Assert.False(chat.IsLive); // An empty buffer doesn't make it live.
        hub.Calls.Clear();

        // Only its last activity changed: a whole turn happened between two board checks.
        hub.RaiseBoard(new BoardDevice(Samples.Desk, "Desk PC", [Samples.Chat("d1", "Fix the parser bug", "idle", -1)], 50));
        await Task.Delay(ChatViewModel.PageSize * 25); // The refresh waits a moment so close changes share one fetch.

        Assert.Contains("History d1 newest", hub.Calls);
    }

    [Fact]
    public async Task A_message_whose_sending_failed_keeps_its_id_when_sent_again()
    {
        var (main, hub, _) = await SignedInAsync();
        var chat = await OpenAsync(main, "d1");
        var fail = true;
        hub.Answer = _ => fail ? throw new InvalidOperationException("Not connected.") : new ChatInputResult(true, "Sent.");

        chat.Draft = "Run the tests";
        await chat.SendCommand.ExecuteAsync(null);
        Assert.True(chat.StatusIsProblem);
        Assert.Contains("won't arrive twice", chat.Status);
        Assert.Equal("Run the tests", chat.Draft);

        fail = false;
        await chat.SendCommand.ExecuteAsync(null);
        Assert.Equal("", chat.Draft);
        Assert.Equal(hub.Sent[0].Input.Id, hub.Sent[1].Input.Id); // The PC applies it once, whichever reached it.

        chat.Draft = "Run the tests";
        await chat.SendCommand.ExecuteAsync(null);
        Assert.NotEqual(hub.Sent[1].Input.Id, hub.Sent[2].Input.Id); // Answered before: this is a new message.
    }

    [Fact]
    public async Task A_refused_phone_says_so_instead_of_connecting_forever()
    {
        var hub = new FakeHub { Refusal = new HttpRequestException("Unauthorized", null, System.Net.HttpStatusCode.Unauthorized) };
        var store = new MemorySecretStore();
        new Enrollment(new Uri("https://hub.example.ts.net/"), Guid.NewGuid(), "Test phone", "secret").Save(store);
        var main = new MainViewModel(store, hub);
        main.Start();
        await Task.Yield();

        Assert.False(main.IsConnected);
        Assert.Contains("refused this phone", main.Chats.Status);
    }

    [Fact]
    public async Task A_board_snapshot_never_undoes_a_newer_update()
    {
        var (main, hub, _) = await SignedInAsync();

        // Newer updates arrive (the laptop leaves, the desk changes) while the snapshot below is older than both.
        hub.RaiseBoard(new BoardDevice(Samples.Laptop, "Laptop", [], 20));
        hub.RaiseBoard(new BoardDevice(Samples.Desk, "Desk PC", [Samples.Chat("d1", "Fix the parser bug", "working", 0)], 21));
        await main.Chats.RefreshAsync(); // hub.Board still holds the sequence 3 and 7 entries.

        Assert.Equal(["Desk PC"], main.Chats.Groups.Select(g => g.Name));
        Assert.Equal("working", Assert.Single(main.Chats.Groups[0].Chats).State);
    }

    [Fact]
    public async Task Settings_follows_the_connection_and_the_board_while_it_shows()
    {
        var (main, hub, _) = await SignedInAsync();
        main.OpenSettings();
        var settings = Assert.IsType<SettingsViewModel>(main.Page);
        var changed = new List<string?>();
        settings.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        hub.SetConnected(false);
        hub.RaiseBoard(new BoardDevice(Samples.Laptop, "Laptop", [], 30));

        Assert.Equal("Not connected", settings.ConnectionText);
        Assert.Contains(nameof(SettingsViewModel.ConnectionText), changed);
        Assert.Contains(nameof(SettingsViewModel.Pcs), changed);
        Assert.Equal(["Desk PC"], settings.Pcs.Select(p => p.Name));
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
    [InlineData("https://example.com/nomad", "https://example.com/nomad/")]
    [InlineData("http://desk.example.ts.net", null)]
    [InlineData("", null)]
    public void Hub_addresses_must_be_https(string address, string? url) =>
        Assert.Equal(url, EnrollLink.CheckAddress(address).Url?.ToString());

    [Fact]
    public void A_message_lists_its_web_and_mail_links_once_each_in_order() =>
        Assert.Equal(
            ["http://example.com/a/", "https://b.example/x?y=1", "mailto:me@example.com"],
            Nomad.Mobile.Markdown.MarkdownBlock.LinksIn(
                "Open http://example.com/a/ then [this one](https://b.example/x?y=1), again http://example.com/a/, "
                + "mail <me@example.com>, a file [here](file:///C:/x.txt), `http://in.code/` and ![pic](https://img.example/p.png)."));

    private static string Encode(AgentEvent e) => RelayCodec.Encode(e) ?? throw new InvalidOperationException($"{e.GetType().Name} isn't relayed.");
}
