using System.Text.Json;
using Avalonia.Threading;
using Nomad.App.Infrastructure;
using Nomad.Core.Agents;
using Nomad.Core.Hub;
using Nomad.Hub.Contracts;

namespace Nomad.App.ViewModels;

/// <summary>
/// This PC's side of the phone (docs/mobile.md): lists the chats open here on the hub's board, and answers for them when the
/// phone asks for a page of a transcript or sends a prompt, an answer or Stop. Everything runs on the UI thread, under the same
/// checks the window uses: an answer must name the card waiting now, and a question's answers are checked whole before any of
/// them is applied.
/// </summary>
internal sealed class MobileHost : IDisposable
{
    /// <summary>How often the board is checked for changes (it is sent only when it changed).</summary>
    private static readonly TimeSpan BoardInterval = TimeSpan.FromSeconds(1);

    /// <summary>
    /// How long an answer waits for the agent to take it before the phone is told it went in (the hub waits 15 s). The hub's
    /// other calls to this PC wait behind it, as SignalR runs them one at a time; the agent normally takes it in milliseconds.
    /// </summary>
    private static readonly TimeSpan AnswerWait = TimeSpan.FromSeconds(5);

    /// <summary>How much of one entry's text is sent at most.</summary>
    private const int MaxText = 12_000;

    private readonly MainViewModel _main;
    private readonly HubClient _client;
    private readonly Func<bool> _enabled;
    private readonly DispatcherTimer _timer;
    private string? _lastBoard;
    private bool _publishing;

    /// <param name="enabled">Whether the phone may see this PC's chats now (the hub relays and Stream live sessions is on).</param>
    public MobileHost(MainViewModel main, HubClient client, Func<bool> enabled)
    {
        _main = main;
        _client = client;
        _enabled = enabled;
        _timer = new DispatcherTimer(BoardInterval, DispatcherPriority.Background, (_, _) => _ = PublishAsync());
        client.HistoryRequested = (backend, id, before, limit) => OnUiAsync(() => Page(backend, id, before, limit));
        client.InputRequested = (backend, id, input, from) => Dispatcher.UIThread.InvokeAsync<ChatInputResult>(() => ApplyAsync(backend, id, input, from));
        client.SettingsRequested = (backend, id) => OnUiAsync(() => SettingsOf(backend, id));
        client.NewChatOptionsRequested = () => Dispatcher.UIThread.InvokeAsync<NewChatOptions>(NewChatOptionsAsync);
        client.StartRequested = (chat, from) => Dispatcher.UIThread.InvokeAsync<NewChatResult>(() => StartAsync(chat, from));
        client.ConnectionChanged += OnConnectionChanged;
        _timer.Start();
        _ = Task.Run(SweepPhoneFiles);
    }

    public void Dispose()
    {
        _timer.Stop();
        _client.ConnectionChanged -= OnConnectionChanged;
        _client.HistoryRequested = null;
        _client.InputRequested = null;
        _client.SettingsRequested = null;
        _client.NewChatOptionsRequested = null;
        _client.StartRequested = null;
    }

    /// <summary>A new connection starts with nothing on the board: the next tick sends it all again.</summary>
    private void OnConnectionChanged(bool up) => Dispatcher.UIThread.Post(() => _lastBoard = null);

    private static Task<T> OnUiAsync<T>(Func<T> work) => Dispatcher.UIThread.InvokeAsync(work).GetTask();


    // ------------------------------------------------------------------ the board

    private async Task PublishAsync()
    {
        if (_publishing || !_client.IsConnected)
        {
            return;
        }

        // Not shared: off the board, so phones show this PC offline (sent once, when it stops sharing or connects).
        var sharing = _enabled();
        List<BoardChat> chats = sharing
            // An archived chat can stay loaded on the PC (still readable there), but it has left the list: off the board too.
            ? [.. _main.ReachableChats.Where(s => !s.IsRelayViewer && _main.RowOf(s) is not { IsArchived: true }).Select(Board),
               .. _main.ClosedChats(ClosedShown).Select(Closed)]
            : [];
        var json = sharing ? JsonSerializer.Serialize(chats, HubJson.Default.IReadOnlyListBoardChat) : NotSharing;
        if (json == _lastBoard)
        {
            return;
        }

        _publishing = true;
        try
        {
            if (sharing)
            {
                await _client.BoardPublishAsync(chats);
            }
            else
            {
                await WithdrawAsync();
            }

            _lastBoard = json;
        }
        catch (Exception e) when (e is InvalidOperationException or Microsoft.AspNetCore.SignalR.HubException or IOException or TaskCanceledException)
        {
            // Not connected, or the hub has no relay: tried again on the next tick.
        }
        finally
        {
            _publishing = false;
        }
    }

    private const string NotSharing = "not sharing";

    /// <summary>Off the board; a hub older than the call gets the empty list it understood as "none shared".</summary>
    private async Task WithdrawAsync()
    {
        try
        {
            await _client.BoardWithdrawAsync();
        }
        catch (Microsoft.AspNetCore.SignalR.HubException e) when (e.Message.Contains("Method does not exist", StringComparison.OrdinalIgnoreCase))
        {
            await _client.BoardPublishAsync([]);
        }
    }

    /// <summary>How many chats from the Idle list go on the board, newest first.</summary>
    private const int ClosedShown = 40;

    private static BoardChat Closed(SessionListItemViewModel r) =>
        new(r.Backend, r.SessionId, r.Title, r.BackendName, r.WorkFolder, "closed", null, "", r.LastActivityUtc, 0);

    private static BoardChat Board(SessionViewModel s) => new(s.Backend, s.SessionId!, s.TitleOrPlaceholder, s.BackendName, s.Folder, State(s),
        Waiting(s), Clip(LastLine(s), 200), s.LastActivityUtc, s.Queued.Count);

    private static string State(SessionViewModel s) =>
        s.PendingPermission is { IsAnswered: false } ? "waiting" : s.IsTurnRunning ? "working" : s.IsProcessRunning ? "idle" : "stopped";

    private static string? Waiting(SessionViewModel s) => s.PendingPermission switch
    {
        { IsAnswered: true } or null => null,
        QuestionItem => "A question",
        { } p => Clip(p.Title, 120),
    };

    private static string LastLine(SessionViewModel s)
    {
        for (var i = s.Items.Count - 1; i >= 0; i--)
        {
            switch (s.Items[i])
            {
                case AssistantTextItem a when a.Text.Length > 0:
                    return a.Text;
                case UserMessageItem u:
                    var summary = u.DisplayText.Length > 0 ? u.DisplayText
                        : u.Files.Count > 0 ? string.Join(", ", u.Files.Select(f => f.Name))
                        : u.Images.Count == 1 ? "Image"
                        : u.Images.Count > 1 ? $"{u.Images.Count} images"
                        : "";
                    return summary.Length > 0 ? "You: " + summary : "";
            }
        }

        return "";
    }

    // A chat from the Idle list is only read while the phone shows it; it opens on the PC when the phone acts on it.
    private SessionViewModel? Find(string backend, string id) =>
        _main.ReachableChats.FirstOrDefault(s => s.Backend == backend && s.SessionId == id && !s.IsRelayViewer)
        ?? _main.PeekForPhone(backend, id);

    /// <summary>
    /// Per chat, a number that changes whenever its items before the end change place (a reload clears them, a rewind or a
    /// folded thinking line removes some): page cursors, which are item positions, hold only within one.
    /// </summary>
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<SessionViewModel, Generation> _generations = new();

    private sealed class Generation
    {
        public int Value;
    }

    private int GenerationOf(SessionViewModel s)
    {
        if (!_generations.TryGetValue(s, out var generation))
        {
            generation = new Generation();
            _generations.Add(s, generation);
            s.Items.CollectionChanged += (_, e) =>
            {
                // Only appends at the end keep earlier positions.
                if (e.Action != System.Collections.Specialized.NotifyCollectionChangedAction.Add || e.NewStartingIndex != s.Items.Count - (e.NewItems?.Count ?? 1))
                {
                    generation.Value++;
                }
            };
        }

        return generation.Value;
    }

    // ------------------------------------------------------------------ transcript pages

    private ChatPage Page(string backend, string id, int? before, int limit)
    {
        if (!_enabled())
        {
            throw new InvalidOperationException("This PC isn't sharing its chats right now.");
        }

        if (Find(backend, id) is not { } s)
        {
            throw new InvalidOperationException("That chat is no longer open in Nomad.");
        }

        var end = Math.Clamp(before ?? s.Items.Count, 0, s.Items.Count);
        var entries = new List<ChatEntry>();
        for (var i = end - 1; i >= 0 && entries.Count < limit; i--)
        {
            if (Entry(i, s.Items[i], ReferenceEquals(s.Items[i], s.PendingPermission)) is { } entry)
            {
                entries.Add(entry);
            }
        }

        entries.Reverse();
        return new ChatPage(State(s), s.Queued.Count, s.Items.Count, entries, GenerationOf(s), s.IsLoadingHistory);
    }

    private const int MaxThumbSide = 320;
    private const int MaxEntryImages = 10;
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Avalonia.Media.Imaging.Bitmap, string> Thumbnails = new();

    private static ChatEntry? Entry(int index, TranscriptItem item, bool pending) => item switch
    {
        UserMessageItem u => new ChatEntry(
            index,
            "user",
            Clip(u.DisplayText),
            u.IsFromChat ? u.FromChatName : u.FromDevice,
            Images: ThumbnailsOf(u.Images),
            Files: u.Files.Count > 0 ? [.. u.Files.Select(f => f.Name)] : null),
        AssistantTextItem a => new ChatEntry(index, "assistant", Clip(a.Text)),
        ToolGroupItem g => new ChatEntry(index, "activity", g.Summary.Length > 0 ? Clip(g.Summary) : $"{g.Calls.Count} tool call{(g.Calls.Count == 1 ? "" : "s")}",
            Steps: Steps(g)),
        ThinkingItem t when t.DisplayText.Length > 0 => new ChatEntry(index, t.IsNarration ? "assistant" : "thinking", Clip(t.DisplayText)),
        QuestionItem q => new ChatEntry(index, "question", "", Card: new ChatCard(q.Request.RequestId, pending && !q.IsAnswered, null, null, null,
            false, null, null, q.AnswerText, [.. q.Questions.Select(e => new ChatQuestion(Clip(e.Header, 200), Clip(e.Question, 2000), e.MultiSelect,
                [.. e.Options.Select(o => new ChatOption(o.Label, o.Description is { } d ? Clip(d, 2000) : null))]))])),
        PermissionItem p => new ChatEntry(index, "permission", "", Card: new ChatCard(p.Request.RequestId, pending && !p.IsAnswered, p.Title,
            Clip(p.Detail, 4000), p.Description is { } d ? Clip(d, 2000) : null, p.CanAllowForSession, p.AllowText, p.DenyText, p.AnswerText, null)),
        ErrorItem e => new ChatEntry(index, "error", Clip(e.Message)),
        NoticeItem n => new ChatEntry(index, "notice", Clip(n.Text)),
        SentFilesItem f => new ChatEntry(index, "notice", f.Caption.Length > 0 ? Clip($"Sent files: {f.Caption}") : "Sent files (open Nomad on the PC to see them)"),
        AssistantImagesItem i => new ChatEntry(index, "notice", $"{i.Sources.Count} image{(i.Sources.Count == 1 ? "" : "s")} (open Nomad on the PC to see them)"),
        ArtifactItem art => new ChatEntry(index, "notice", Clip($"Artifact: {art.Title} {art.Url}")),
        NewChatItem chat => new ChatEntry(index, "notice", Clip($"Suggested a new chat: {chat.Title}")),
        _ => null,
    };

    private static IReadOnlyList<string>? ThumbnailsOf(IReadOnlyList<Avalonia.Media.Imaging.Bitmap> images)
    {
        if (images.Count == 0)
        {
            return null;
        }

        List<string>? encoded = null;
        foreach (var bitmap in images.Take(MaxEntryImages))
        {
            if (ThumbnailOf(bitmap) is { } thumb)
            {
                (encoded ??= []).Add(thumb);
            }
        }

        return encoded;
    }

    private static string? ThumbnailOf(Avalonia.Media.Imaging.Bitmap bitmap)
    {
        if (Thumbnails.TryGetValue(bitmap, out var cached))
        {
            return cached.Length > 0 ? cached : null;
        }

        var encoded = EncodeThumbnail(bitmap);
        Thumbnails.AddOrUpdate(bitmap, encoded ?? "");
        return encoded;
    }

    private static string? EncodeThumbnail(Avalonia.Media.Imaging.Bitmap bitmap)
    {
        try
        {
            var size = bitmap.PixelSize;
            if (size.Width <= 0 || size.Height <= 0)
            {
                return null;
            }

            var longest = Math.Max(size.Width, size.Height);
            var scaled = longest > MaxThumbSide
                ? bitmap.CreateScaledBitmap(
                    new Avalonia.PixelSize(Math.Max(1, size.Width * MaxThumbSide / longest), Math.Max(1, size.Height * MaxThumbSide / longest)),
                    Avalonia.Media.Imaging.BitmapInterpolationMode.HighQuality)
                : null;
            try
            {
                using var png = new MemoryStream();
                (scaled ?? bitmap).Save(png);
                var bytes = png.ToArray();
                if (bytes.Length > 24_000)
                {
                    using var sk = SkiaSharp.SKImage.FromEncodedData(bytes);
                    if (sk is not null)
                    {
                        using var jpg = sk.Encode(SkiaSharp.SKEncodedImageFormat.Jpeg, 80);
                        if (jpg is not null && jpg.Size < bytes.Length)
                        {
                            bytes = jpg.ToArray();
                        }
                    }
                }

                return Convert.ToBase64String(bytes);
            }
            finally
            {
                scaled?.Dispose();
            }
        }
        catch (Exception)
        {
            return null;
        }
    }

    // ------------------------------------------------------------------ input

    /// <summary>
    /// The latest inputs by their id, with how they were answered: a retry gets the same answer and is applied once, even while
    /// the first is still going (an answer waits for the agent). A refused input is forgotten once refused, so it can be tried again.
    /// </summary>
    private readonly Dictionary<string, Task<ChatInputResult>> _applied = new(StringComparer.Ordinal);
    private readonly Queue<string> _appliedOrder = new();

    private Task<ChatInputResult> ApplyAsync(string backend, string id, ChatInput input, string from)
    {
        if (input.Id is not { Length: > 0 } key)
        {
            return ApplyOnceAsync(backend, id, input, from);
        }

        if (_applied.TryGetValue(key, out var earlier))
        {
            return earlier;
        }

        var result = ApplyOnceAsync(backend, id, input, from);
        _applied[key] = result;
        _appliedOrder.Enqueue(key);
        while (_appliedOrder.Count > 200)
        {
            _applied.Remove(_appliedOrder.Dequeue());
        }

        _ = ForgetIfRefusedAsync(key, result);
        return result;
    }

    private async Task ForgetIfRefusedAsync(string key, Task<ChatInputResult> result)
    {
        try
        {
            await result;
        }
        catch
        {
            // A failure is the caller's to report; here it only means the input wasn't applied.
        }

        if ((result.IsFaulted || result.IsCanceled || !result.Result.Ok) && _applied.TryGetValue(key, out var kept) && ReferenceEquals(kept, result))
        {
            _applied.Remove(key);
            var rest = _appliedOrder.Where(k => k != key).ToList();
            _appliedOrder.Clear();
            foreach (var k in rest)
            {
                _appliedOrder.Enqueue(k);
            }
        }
    }

    /// <summary>Where files phones attach are kept: beside the settings, one folder per file, so names never collide.</summary>
    internal static string PhoneFilesFolder => Path.Combine(Path.GetDirectoryName(Program.SettingsPath)!, "phone-files");

    /// <summary>
    /// How long a prompt's files may take to come from the hub: the hub gives up on this PC's answer after 15 seconds, and other
    /// calls from it wait behind this one.
    /// </summary>
    private static readonly TimeSpan FetchBudget = TimeSpan.FromSeconds(9);

    /// <summary>Downloads a phone prompt's files from the hub, each into a folder of its own, and makes them attachments.</summary>
    private async Task<List<Attachment>> FetchFilesAsync(IReadOnlyList<ChatFile> files)
    {
        var attachments = new List<Attachment>();
        if (files.Count == 0)
        {
            return attachments;
        }

        using var budget = new CancellationTokenSource(FetchBudget);
        foreach (var file in files)
        {
            // The hub's ids are 32 hex digits; the name is kept to a plain file name, so nothing lands outside its folder.
            if (file?.Id is not { Length: 32 } || !file.Id.All(char.IsAsciiHexDigit))
            {
                throw new IOException("the phone named a file the hub doesn't have.");
            }

            var dir = Path.Combine(PhoneFilesFolder, file.Id.ToLowerInvariant());
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, SafeFileName(file.Name));
            if (!File.Exists(path))
            {
                var partial = path + ".part";
                try
                {
                    await using (var source = await _client.DownloadFileAsync(file.Id, budget.Token))
                    await using (var target = File.Create(partial))
                    {
                        await source.CopyToAsync(target, budget.Token);
                    }

                    File.Move(partial, path, overwrite: true);
                }
                catch
                {
                    // Nothing half-fetched left behind.
                    try
                    {
                        File.Delete(partial);
                        if (!Directory.EnumerateFileSystemEntries(dir).Any())
                        {
                            Directory.Delete(dir);
                        }
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                    }

                    throw;
                }
            }

            // Read (and an image decoded, and scaled down if large) away from the window.
            attachments.Add(await Task.Run(() => PromptImages.FromPath(path)));
        }

        return attachments;
    }

    private static readonly HashSet<string> DeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>A plain file name Windows will make: no folders, no characters it refuses, not a device's name.</summary>
    internal static string SafeFileName(string? name)
    {
        var clean = string.Concat((name ?? "").Replace('\\', '/').Split('/')[^1].Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim().TrimEnd('.');
        if (clean.Length == 0 || clean is "." or "..")
        {
            return "file";
        }

        return DeviceNames.Contains(clean.Split('.')[0].TrimEnd()) ? "_" + clean : clean;
    }

    /// <summary>How long files phones sent are kept on this PC: long enough for a chat to come back to them.</summary>
    private static readonly TimeSpan KeepPhoneFiles = TimeSpan.FromDays(30);

    /// <summary>Removes files phones sent more than <see cref="KeepPhoneFiles"/> ago (once, when Nomad starts).</summary>
    private static void SweepPhoneFiles()
    {
        try
        {
            if (!Directory.Exists(PhoneFilesFolder))
            {
                return;
            }

            foreach (var dir in Directory.GetDirectories(PhoneFilesFolder))
            {
                if (Directory.GetLastWriteTimeUtc(dir) < DateTime.UtcNow - KeepPhoneFiles)
                {
                    try
                    {
                        Directory.Delete(dir, recursive: true);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        // In use or locked: next time.
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private async Task<ChatInputResult> ApplyOnceAsync(string backend, string id, ChatInput input, string from)
    {
        if (!_enabled())
        {
            return new ChatInputResult(false, "This PC isn't sharing its chats right now.");
        }

        if (Find(backend, id) is not { } s)
        {
            return new ChatInputResult(false, "That chat is no longer open in Nomad.");
        }

        // Read from the Idle list until now: a prompt or a setting opens it on the PC, as clicking its row there does.
        // Its name, pin and archive are the list row's, so those leave it where it is.
        if (input.Kind is not ("rename" or "pin" or "archive") && _main.IsPeeked(s))
        {
            s = _main.OpenPeeked(s);
        }

        switch (input.Kind)
        {
            case "prompt":
                var text = input.Text?.Trim() ?? "";
                var files = input.Files ?? [];
                if (text.Length == 0 && files.Count == 0)
                {
                    return new ChatInputResult(false, "Nothing to send.");
                }

                // Its files first, onto this PC, then attached as if attached here: an image as an image, anything else by path.
                List<Attachment> attachments;
                try
                {
                    attachments = await FetchFilesAsync(files);
                }
                catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or OperationCanceledException)
                {
                    return new ChatInputResult(false, ex is OperationCanceledException
                        ? "Its files took too long to reach the PC. Try fewer or smaller ones."
                        : "Its files couldn't be fetched onto the PC: " + ex.Message);
                }

                // As if typed on the PC. Answered within a few seconds, not once the agent has started (that can take a while),
                // so a slow start never looks failed.
                var delivery = s.ReceivePromptAsync(text, from, attachments);
                if (await Task.WhenAny(delivery, Task.Delay(TimeSpan.FromSeconds(3))) != delivery)
                {
                    return new ChatInputResult(true, "Sent.");
                }

                return await delivery switch
                {
                    MessageDelivery.IntoTurn => new ChatInputResult(true, "Sent into the running turn: it reads it at its next step."),
                    MessageDelivery.Queued => new ChatInputResult(true, "Queued: it is working, and gets this when the turn ends."),
                    MessageDelivery.RunningElsewhere => new ChatInputResult(false,
                        "This session is running in another Nomad window or a terminal on that PC. End it there first."),
                    MessageDelivery.Failed => new ChatInputResult(false,
                        s.Items.LastOrDefault() is ErrorItem error ? "It couldn't be sent: " + error.Message : "It couldn't be sent. Look at the chat on the PC."),
                    _ => new ChatInputResult(true, "Sent."),
                };

            case "stop":
                if (!s.IsTurnRunning)
                {
                    return new ChatInputResult(false, "It isn't working on anything.");
                }

                // Answered at once: stopping can take a moment, and the PC's other messages from the hub wait behind this one.
                _ = s.StopAsync();
                return new ChatInputResult(true, "Stopping.");

            case "answer":
                return await AnswerAsync(s, input, from);

            case "model" or "effort" or "mode":
                return await ChangeSettingAsync(s, input.Kind, input.Choice);

            case "rename":
                var title = input.Text?.ReplaceLineEndings(" ").Trim();
                if (string.IsNullOrEmpty(title))
                {
                    return new ChatInputResult(false, "A chat needs a name.");
                }

                return _main.RenameFromPhone(s, title)
                    ? new ChatInputResult(true, "Renamed.")
                    : new ChatInputResult(false, "It couldn't be renamed. Look at the chat on the PC.");

            case "pin":
                if (_main.RowOf(s) is not { } pinRow || input.Choice is not ("on" or "off"))
                {
                    return new ChatInputResult(false, "It can't be pinned now.");
                }

                if (pinRow.IsPinned != (input.Choice == "on"))
                {
                    await _main.TogglePinnedAsync(pinRow);
                }

                return new ChatInputResult(true, pinRow.IsPinned ? "Pinned." : "Unpinned.");

            case "archive":
                if (_main.RowOf(s) is not { CanManage: true } archiveRow)
                {
                    return new ChatInputResult(false, "This chat can't be archived from here.");
                }

                // Answered at once: archiving ends the chat's process, which can take a moment.
                _ = _main.SetArchivedAsync([archiveRow], archive: true);
                return new ChatInputResult(true, "Archived.");

            default:
                return new ChatInputResult(false, "Nomad doesn't know that kind of input.");
        }
    }

    // ------------------------------------------------------------------ settings and new chats

    /// <summary>A chat's model, effort and mode, with the choices its menus on the PC offer.</summary>
    private ChatSettings SettingsOf(string backend, string id)
    {
        if (!_enabled())
        {
            throw new InvalidOperationException("This PC isn't sharing its chats right now.");
        }

        return Find(backend, id) is { } s
            ? SettingsOf(s) with { Title = _main.RowOf(s)?.Title ?? s.TitleOrPlaceholder, Pinned = _main.RowOf(s)?.IsPinned ?? false }
            : throw new InvalidOperationException("That chat is no longer open in Nomad.");
    }

    private static ChatSettings SettingsOf(SessionViewModel s)
    {
        var models = s.ModelOptions.Concat(s.OtherModelOptions).DistinctBy(o => o.Id)
            .Select(o => new ChatChoice(o.Id, o.Label, o.Description)).ToList();
        if (!string.IsNullOrEmpty(s.Model) && models.All(m => m.Id != s.Model))
        {
            models.Insert(0, new ChatChoice(s.Model, Catalog.ModelName(s.Model)));
        }

        return new ChatSettings(
            s.Model, models,
            s.Effort, s.HasEffortLevels ? [.. s.EffortStops.Select(e => new ChatChoice(e.Id, e.DisplayName))] : [],
            s.PermissionMode, [.. s.ModeOptions.Select(o => new ChatChoice(o.Id, o.Label, o.Description))]);
    }

    /// <summary>
    /// A model, effort or mode picked on the phone, applied as if picked in the chat's menu here. A model switch asks no second
    /// time: picking it on the phone is the choice.
    /// </summary>
    private static async Task<ChatInputResult> ChangeSettingAsync(SessionViewModel s, string kind, string? choice)
    {
        var settings = SettingsOf(s);
        var (choices, name) = kind switch
        {
            "model" => (settings.Models, "model"),
            "effort" => (settings.Efforts, "effort"),
            _ => (settings.Modes, "mode"),
        };
        if (choice is null || choices.FirstOrDefault(c => c.Id == choice) is not { } picked)
        {
            return new ChatInputResult(false, $"That {name} isn't one this chat offers.");
        }

        switch (kind)
        {
            case "model":
                await s.SelectModelAsync(picked.Id);
                if (s.PendingModel is not null)
                {
                    await s.ConfirmModelSwitchCommand.ExecuteAsync("once");
                }

                return s.Model == picked.Id
                    ? new ChatInputResult(true, $"Model: {picked.Name}.")
                    : new ChatInputResult(false, "The model didn't change. Look at the chat on the PC.");

            case "effort":
                await s.SelectEffortAsync(picked.Id);
                return new ChatInputResult(true, $"Effort: {picked.Name}.");

            default:
                await s.SelectModeAsync(picked.Id);
                return s.PermissionMode == picked.Id
                    ? new ChatInputResult(true, $"Mode: {picked.Name}.")
                    : new ChatInputResult(false, "The mode didn't change. Look at the chat on the PC.");
        }
    }

    /// <summary>
    /// Each agent as a new chat here starts on it, and the folders used lately. Codex's and OpenCode's models come from their
    /// own servers: asked for first, as picking that agent here would, for a few seconds at most.
    /// </summary>
    private async Task<NewChatOptions> NewChatOptionsAsync()
    {
        if (!_enabled())
        {
            throw new InvalidOperationException("This PC isn't sharing its chats right now.");
        }

        await Task.WhenAny(Task.WhenAll(_main.LoadCodexCatalogAsync(), _main.LoadOpenCodeCatalogAsync()), Task.Delay(CatalogWait));

        var agents = AgentBackends.All
            .Select(b => new NewChatAgent(b.Id, b.Name, SettingsOf(new SessionViewModel(_main, null, b.Id))))
            .ToList();
        return new NewChatOptions(agents, AgentBackends.Normalize(_main.Settings.DefaultBackend), _main.RecentFolders);
    }

    // Chats started for a phone's request id, so a retry doesn't start a second one.
    private readonly Dictionary<string, Task<NewChatResult>> _started = [];

    private async Task<NewChatResult> StartAsync(NewChat chat, string from)
    {
        if (chat.Id is not { } key)
        {
            return await StartOnceAsync(chat, from);
        }

        if (!_started.TryGetValue(key, out var started))
        {
            if (_started.Count > 50)
            {
                _started.Clear();
            }

            _started[key] = started = StartOnceAsync(chat, from);
        }

        // A refusal or failure started nothing, so it isn't kept: the same request tried again (sharing turned on, the folder
        // made) gets a fresh answer.
        try
        {
            var result = await started;
            if (!result.Ok)
            {
                ForgetStart(key, started);
            }

            return result;
        }
        catch
        {
            ForgetStart(key, started);
            throw;
        }
    }

    private void ForgetStart(string key, Task<NewChatResult> started)
    {
        if (_started.TryGetValue(key, out var kept) && kept == started)
        {
            _started.Remove(key);
        }
    }

    /// <summary>How long a start waits for the new chat's session id. Kept short, since the hub's other calls to this PC wait
    /// behind it (see <see cref="AnswerWait"/>); a slower start answers without the id and the phone finds the chat on the board.</summary>
    private static readonly TimeSpan StartWait = TimeSpan.FromSeconds(4);

    /// <summary>How long new-chat options wait for the Codex and OpenCode model lists, for the same reason.</summary>
    private static readonly TimeSpan CatalogWait = TimeSpan.FromSeconds(4);

    /// <summary>
    /// Starts a chat as the new-session page here would, but in the background (the PC's window stays where it is), and sends it
    /// the phone's first message. Answered once it has a session id, or after <see cref="StartWait"/> without one (the phone then
    /// finds it on the board).
    /// </summary>
    private async Task<NewChatResult> StartOnceAsync(NewChat chat, string from)
    {
        if (!_enabled())
        {
            return new NewChatResult(false, "This PC isn't sharing its chats right now.");
        }

        var prompt = chat.Prompt.Trim();
        if (prompt.Length == 0)
        {
            return new NewChatResult(false, "A new chat needs a first message.");
        }

        if (AgentBackends.All.All(b => b.Id != chat.Backend))
        {
            return new NewChatResult(false, "Nomad doesn't know that agent.");
        }

        // Only a folder the PC offered (its recent ones), so a phone can't point it anywhere else, a network share included.
        if (chat.Folder is { } folder
            && (!_main.RecentFolders.Any(f => string.Equals(Path.TrimEndingDirectorySeparator(f), Path.TrimEndingDirectorySeparator(folder), StringComparison.OrdinalIgnoreCase))
                || !Directory.Exists(folder)))
        {
            return new NewChatResult(false, "That folder isn't on this PC's list.");
        }

        var session = _main.StartPhoneChat(chat.Backend, chat.Folder);
        if (chat.Model is { } model && SettingsOf(session).Models.Any(m => m.Id == model))
        {
            await session.SelectModelAsync(model);
        }

        if (chat.Effort is { } effort && SettingsOf(session).Efforts.Any(e => e.Id == effort))
        {
            await session.SelectEffortAsync(effort);
        }

        if (chat.Mode is { } mode && SettingsOf(session).Modes.Any(m => m.Id == mode))
        {
            await session.SelectModeAsync(mode);
        }

        var delivery = session.ReceivePromptAsync(prompt, from);
        var waitUntil = DateTime.UtcNow + StartWait;
        while (string.IsNullOrEmpty(session.SessionId) && DateTime.UtcNow < waitUntil)
        {
            if (delivery.IsCompleted && delivery.Result == MessageDelivery.Failed)
            {
                _main.DropPhoneChat(session);
                return new NewChatResult(false,
                    session.Items.LastOrDefault() is ErrorItem error ? "It couldn't start: " + error.Message : "It couldn't start. Look at Nomad on the PC.");
            }

            await Task.Delay(250);
        }

        return new NewChatResult(true, "Started.", string.IsNullOrEmpty(session.SessionId) ? null : session.SessionId);
    }

    /// <summary>
    /// Answers the waiting card: only the one the phone names, only with a choice it offers, and for a question only when every
    /// question gets a valid answer (nothing on the card changes otherwise). "Answered." waits until the agent has it, so a
    /// failure to deliver it reaches the phone rather than only the PC's chat.
    /// </summary>
    private static async Task<ChatInputResult> AnswerAsync(SessionViewModel s, ChatInput input, string from)
    {
        if (s.PendingPermission is not { IsAnswered: false } pending || input.Card is null || pending.Request.RequestId != input.Card)
        {
            return new ChatInputResult(false, "That request is no longer the one waiting.");
        }

        PermissionChoice? choice = input.Choice switch
        {
            "allow" => PermissionChoice.Allow,
            "session" when pending.CanAllowForSession && pending is not QuestionItem => PermissionChoice.AllowForSession,
            "deny" => PermissionChoice.Deny,
            _ => null,
        };
        if (choice is not { } picked)
        {
            return new ChatInputResult(false, "That choice isn't offered for this request.");
        }

        if (pending is QuestionItem question && picked != PermissionChoice.Deny)
        {
            if (ReadAnswers(question, input.Answers) is not { } answers)
            {
                return new ChatInputResult(false, "Every question needs an answer: pick an option or type one.");
            }

            ApplyAnswers(question, answers);
        }

        // The card is marked answered (or refused) before this awaits anything.
        var sent = s.AnswerPendingAsync(picked);
        if (!pending.IsAnswered)
        {
            return new ChatInputResult(false, await sent ?? "Nomad didn't take that answer. Look at the chat on the PC.");
        }

        pending.MarkAnswered($"{pending.AnswerText} (on {from})"); // The card says where it was answered, as a relayed answer does.
        try
        {
            return await sent.WaitAsync(AnswerWait) is { } failed
                ? new ChatInputResult(false, failed)
                : new ChatInputResult(true, "Answered.");
        }
        catch (TimeoutException)
        {
            // Still on its way: if it fails after all, the error shows in the chat, which the phone reads.
            return new ChatInputResult(true, "Answered. The agent hasn't confirmed it yet, so watch the chat.");
        }
    }

    /// <summary>The chosen options and typed text for each question, or null unless every question gets a valid answer.</summary>
    private static List<(HashSet<QuestionOption> Options, string Other)>? ReadAnswers(QuestionItem question, IReadOnlyList<ChatAnswer>? answers)
    {
        if (answers is null || answers.Count != question.Questions.Count)
        {
            return null;
        }

        var result = new List<(HashSet<QuestionOption>, string)>();
        for (var i = 0; i < answers.Count; i++)
        {
            var entry = question.Questions[i];
            var picked = new HashSet<QuestionOption>();
            foreach (var label in answers[i]?.Options ?? [])
            {
                if (entry.Options.FirstOrDefault(o => o.Label == label) is not { } option)
                {
                    return null;
                }

                picked.Add(option);
            }

            var other = (answers[i]?.Other ?? "").Trim();
            if ((picked.Count == 0 && other.Length == 0) || (!entry.MultiSelect && picked.Count + (other.Length > 0 ? 1 : 0) > 1))
            {
                return null;
            }

            result.Add((picked, other));
        }

        return result;
    }

    /// <summary>Puts validated answers on the card through its own Pick and OtherText, so its state stays consistent.</summary>
    private static void ApplyAnswers(QuestionItem question, List<(HashSet<QuestionOption> Options, string Other)> answers)
    {
        for (var i = 0; i < answers.Count; i++)
        {
            var entry = question.Questions[i];
            var (picked, other) = answers[i];
            if (entry.MultiSelect)
            {
                foreach (var option in entry.Options.Where(o => o.IsSelected != picked.Contains(o)).ToList())
                {
                    entry.Pick(option);
                }

                entry.OtherText = other;
            }
            else if (picked.Count == 1)
            {
                entry.Pick(picked.First());
            }
            else
            {
                entry.OtherText = other;
            }
        }
    }

    /// <summary>Most steps an activity sends, newest kept.</summary>
    private const int MaxSteps = 60;

    /// <summary>
    /// What an activity group did: its tool calls with their one line, and the thinking between them. They share the entry's own
    /// limit with its summary, so a page stays as large as it was before steps were sent.
    /// </summary>
    private static List<ChatStep> Steps(ToolGroupItem group)
    {
        var steps = new List<ChatStep>();
        var budget = MaxText - Math.Min(group.Summary.Length, MaxText);
        // Newest first, so a long group keeps what it is doing now; put back in order at the end.
        foreach (var entry in group.Entries.Reverse().Take(MaxSteps))
        {
            ChatStep? step = entry switch
            {
                ToolCallItem call => new ChatStep("tool", call.Name, Clip(OneLine(call.Summary.Length > 0 ? call.Summary : ToolText.PrettyJson(call.Input)), 400),
                    call.Status == ToolStatus.Failed),
                ThinkingItem { DisplayText.Length: > 0 } thinking => new ChatStep("thinking", "Thought", Clip(thinking.DisplayText, 2_000)),
                _ => null,
            };
            if (step is null)
            {
                continue;
            }

            budget -= step.Title.Length + (step.Detail?.Length ?? 0);
            if (budget < 0)
            {
                break;
            }

            steps.Add(step);
        }

        steps.Reverse();
        return steps;
    }

    private static string OneLine(string text) => text.ReplaceLineEndings(" ").Trim();

    /// <summary>Cuts text to <paramref name="max"/> characters, never between the two halves of a surrogate pair.</summary>
    internal static string Clip(string text, int max = MaxText)
    {
        if (text.Length <= max)
        {
            return text;
        }

        var cut = char.IsHighSurrogate(text[max - 1]) ? max - 1 : max;
        return text[..cut] + "…";
    }
}
