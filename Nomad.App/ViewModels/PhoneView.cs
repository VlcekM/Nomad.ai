using System.Buffers;
using System.Net.Sockets;
using System.Text.Json;
using Avalonia.Threading;
using Nomad.Core.Agents;
using Nomad.Core.Phone;

namespace Nomad.App.ViewModels;

/// <summary>
/// The phone view (Settings › Remote Control): a small web page Nomad serves on 127.0.0.1 that lists the open chats of every
/// agent, shows a chat's transcript as it grows, sends prompts into it (queued behind a running turn, as typed) and answers
/// its permission and question cards. Carrying the port to a phone is left to the user (a tunnel or a private network);
/// every request but the page itself needs the access key, which the copied link carries in its fragment.
/// </summary>
internal sealed class PhoneView(MainViewModel main) : IDisposable
{
    /// <summary>How many transcript items a chat sends at most (the newest).</summary>
    private const int MaxItems = 80;

    /// <summary>How much of one item's text is sent at most.</summary>
    private const int MaxText = 12_000;

    private PhoneHttpServer? _server;

    /// <summary>The one phone view of this window, set at startup.</summary>
    public static PhoneView? Current { get; set; }

    /// <summary>"Serving on …", why it could not start, or "Off".</summary>
    public string Status { get; private set; } = "Off";

    /// <summary>Starts, restarts or stops the server to match the settings (a new port or key restarts it).</summary>
    public void Apply()
    {
        _server?.Dispose();
        _server = null;
        var s = main.Settings;
        if (!s.PhoneView)
        {
            Status = "Off";
            return;
        }

        if (string.IsNullOrEmpty(s.PhoneViewKey))
        {
            s.PhoneViewKey = PhoneHttpServer.NewKey();
            main.SaveSettings();
        }

        try
        {
            _server = PhoneHttpServer.Start(s.PhoneViewPort, s.PhoneViewKey, HandleAsync);
            Status = $"Serving on 127.0.0.1:{_server.Port}, on this PC only.";
        }
        catch (SocketException e)
        {
            Status = $"Could not start on port {s.PhoneViewPort}: {e.Message}";
        }
    }

    /// <summary>The link to open on the phone: the address it uses (or this PC's), with the key in the fragment.</summary>
    public string Link()
    {
        var s = main.Settings;
        var address = string.IsNullOrWhiteSpace(s.PhoneViewAddress) ? $"http://127.0.0.1:{s.PhoneViewPort}" : s.PhoneViewAddress.Trim().TrimEnd('/');
        return $"{address}/#k={s.PhoneViewKey}";
    }

    public void Dispose()
    {
        _server?.Dispose();
        _server = null;
    }

    // ------------------------------------------------------------------ requests

    private async Task<PhoneResponse> HandleAsync(PhoneRequest request)
    {
        string Query(string name) => request.Query.TryGetValue(name, out var value) ? value : "";

        return (request.Method, request.Path) switch
        {
            ("GET", "/") => PhoneResponse.Html(PhoneViewPage.Html),
            ("GET", "/api/chats") => PhoneResponse.Json(await OnUiAsync(ListChats)),
            ("GET", "/api/chat") => await OnUiAsync(() => Chat(Query("id"), Query("v"))),
            ("POST", "/api/send") => await OnUiAsync(() => Send(Query("id"), request.Body)),
            ("POST", "/api/answer") => await OnUiAsync(() => Answer(Query("id"), request.Body)),
            _ => PhoneResponse.Text(404, "Not found"),
        };
    }

    private static Task<T> OnUiAsync<T>(Func<T> work) => Dispatcher.UIThread.InvokeAsync(work).GetTask();

    private SessionViewModel? Find(string id) => main.ReachableChats.FirstOrDefault(s => s.ChatKey == id);

    private static PhoneResponse Gone() => PhoneResponse.Text(404, "That chat is no longer open in Nomad.");

    private static string State(SessionViewModel s) =>
        s.PendingPermission is { IsAnswered: false } ? "waiting" : s.IsTurnRunning ? "working" : s.IsProcessRunning ? "idle" : "stopped";

    private byte[] ListChats() => Write(w =>
    {
        w.WriteStartArray();
        foreach (var s in main.ReachableChats)
        {
            w.WriteStartObject();
            w.WriteString("id", s.ChatKey);
            w.WriteString("title", s.TitleOrPlaceholder);
            w.WriteString("agent", s.BackendName);
            w.WriteString("folder", s.Folder ?? "");
            w.WriteString("state", State(s));
            w.WriteString("last", Clip(LastText(s), 160));
            w.WriteEndObject();
        }

        w.WriteEndArray();
    });

    private static string LastText(SessionViewModel s)
    {
        for (var i = s.Items.Count - 1; i >= 0; i--)
        {
            switch (s.Items[i])
            {
                case AssistantTextItem a when a.Text.Length > 0:
                    return a.Text;
                case UserMessageItem u:
                    return "You: " + u.DisplayText;
            }
        }

        return "";
    }

    /// <summary>
    /// A cheap fingerprint of what <see cref="Chat"/> would send: the state, the queue, the item count and the shape of the
    /// newest items (the only ones that still change), so an unchanged chat is answered without serializing it.
    /// </summary>
    private static string Version(SessionViewModel s)
    {
        var hash = new HashCode();
        hash.Add(State(s));
        hash.Add(s.Queued.Count);
        hash.Add(s.Items.Count);
        hash.Add(s.PendingPermission);
        for (var i = Math.Max(0, s.Items.Count - 4); i < s.Items.Count; i++)
        {
            var item = s.Items[i];
            hash.Add(item);
            hash.Add(item switch
            {
                AssistantTextItem a => a.Text.Length,
                ToolGroupItem g => g.Summary.Length * 1000 + g.Calls.Count,
                PermissionItem p => p.IsAnswered ? 1 : 0,
                _ => 0,
            });
        }

        return hash.ToHashCode().ToString("x8", System.Globalization.CultureInfo.InvariantCulture);
    }

    private PhoneResponse Chat(string id, string known)
    {
        if (Find(id) is not { } s)
        {
            return Gone();
        }

        var version = Version(s);
        if (version == known)
        {
            return PhoneResponse.NotModified;
        }

        return PhoneResponse.Json(Write(w =>
        {
            w.WriteStartObject();
            w.WriteString("id", s.ChatKey);
            w.WriteString("v", version);
            w.WriteString("title", s.TitleOrPlaceholder);
            w.WriteString("agent", s.BackendName);
            w.WriteString("state", State(s));
            w.WriteNumber("queued", s.Queued.Count);
            var start = Math.Max(0, s.Items.Count - MaxItems);
            w.WriteBoolean("truncated", start > 0);
            w.WriteStartArray("items");
            for (var i = start; i < s.Items.Count; i++)
            {
                WriteItem(w, s.Items[i], ReferenceEquals(s.Items[i], s.PendingPermission));
            }

            w.WriteEndArray();
            w.WriteEndObject();
        }));
    }

    private static void WriteItem(Utf8JsonWriter w, TranscriptItem item, bool pending)
    {
        switch (item)
        {
            case UserMessageItem u:
                w.WriteStartObject();
                w.WriteString("kind", "user");
                w.WriteString("text", Clip(u.DisplayText));
                if (u.IsFromChat)
                {
                    w.WriteString("from", u.FromChatName);
                }

                w.WriteEndObject();
                break;
            case AssistantTextItem a:
                Simple(w, "assistant", a.Text);
                break;
            case ToolGroupItem g:
                Simple(w, "activity", g.Summary.Length > 0 ? g.Summary : $"{g.Calls.Count} tool call{(g.Calls.Count == 1 ? "" : "s")}");
                break;
            case QuestionItem q:
                w.WriteStartObject();
                w.WriteString("kind", "question");
                w.WriteString("card", q.Request.RequestId);
                w.WriteBoolean("pending", pending && !q.IsAnswered);
                w.WriteString("answer", q.AnswerText);
                w.WriteStartArray("questions");
                foreach (var entry in q.Questions)
                {
                    w.WriteStartObject();
                    w.WriteString("question", Clip(entry.Question, 2000));
                    w.WriteString("header", Clip(entry.Header, 200));
                    w.WriteBoolean("multi", entry.MultiSelect);
                    w.WriteStartArray("options");
                    foreach (var option in entry.Options)
                    {
                        w.WriteStartObject();
                        w.WriteString("label", Clip(option.Label, 500));
                        w.WriteString("description", Clip(option.Description ?? "", 2000));
                        w.WriteEndObject();
                    }

                    w.WriteEndArray();
                    w.WriteEndObject();
                }

                w.WriteEndArray();
                w.WriteEndObject();
                break;
            case PermissionItem p:
                w.WriteStartObject();
                w.WriteString("kind", "permission");
                w.WriteString("card", p.Request.RequestId);
                w.WriteString("title", p.Title);
                w.WriteString("detail", Clip(p.Detail, 4000));
                w.WriteString("description", Clip(p.Description ?? "", 2000));
                w.WriteBoolean("pending", pending && !p.IsAnswered);
                w.WriteBoolean("canSession", p.CanAllowForSession);
                w.WriteString("allowText", p.AllowText);
                w.WriteString("denyText", p.DenyText);
                w.WriteString("answer", p.AnswerText);
                w.WriteEndObject();
                break;
            case ErrorItem e:
                Simple(w, "error", e.Message);
                break;
            case NoticeItem n:
                Simple(w, "notice", n.Text);
                break;
            case SentFilesItem f:
                Simple(w, "notice", f.Caption.Length > 0 ? $"Sent files: {f.Caption}" : "Sent files (open Nomad on the PC to see them)");
                break;
            case AssistantImagesItem i:
                Simple(w, "notice", $"{i.Sources.Count} image{(i.Sources.Count == 1 ? "" : "s")} (open Nomad on the PC to see them)");
                break;
            case ArtifactItem art:
                Simple(w, "notice", $"Artifact: {art.Title} {art.Url}");
                break;
            case NewChatItem chat:
                Simple(w, "notice", $"Suggested a new chat: {chat.Title}");
                break;
        }
    }

    private static void Simple(Utf8JsonWriter w, string kind, string text)
    {
        w.WriteStartObject();
        w.WriteString("kind", kind);
        w.WriteString("text", Clip(text));
        w.WriteEndObject();
    }

    /// <summary>
    /// Sends <c>{"text": …}</c> into the chat as if typed: queued behind a running turn, sent now otherwise. Answers once the
    /// prompt is queued, not when the agent has started (that can take a while), so a slow start never looks like a failure.
    /// </summary>
    private PhoneResponse Send(string id, byte[] body)
    {
        if (Find(id) is not { } s)
        {
            return Gone();
        }

        using var doc = Parse(body);
        var text = doc is { RootElement.ValueKind: JsonValueKind.Object } && doc.RootElement.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String
            ? t.GetString()?.Trim()
            : null;
        if (string.IsNullOrEmpty(text))
        {
            return PhoneResponse.Text(400, "Nothing to send.");
        }

        var busy = s.IsTurnRunning;
        _ = s.ReceiveMessageAsync(text);
        return PhoneResponse.Text(200, busy ? "Queued: it is working, and gets this when the turn ends." : "Sent.");
    }

    /// <summary>
    /// Answers one card: <c>{"card": request id, "choice": "allow" | "session" | "deny"}</c>, and for a question card
    /// <c>"answers": [{"options": [labels], "other": "text"}, …]</c> in the order of its questions. The card must still be
    /// the one waiting, so an answer meant for one request never lands on another; nothing changes unless the whole answer
    /// is valid.
    /// </summary>
    private PhoneResponse Answer(string id, byte[] body)
    {
        if (Find(id) is not { } s)
        {
            return Gone();
        }

        using var doc = Parse(body);
        if (doc is not { RootElement.ValueKind: JsonValueKind.Object })
        {
            return PhoneResponse.Text(400, "Not a JSON object.");
        }

        var root = doc.RootElement;
        var card = StringOf(root, "card");
        if (s.PendingPermission is not { IsAnswered: false } pending || card is null || pending.Request.RequestId != card)
        {
            return PhoneResponse.Text(409, "That request is no longer the one waiting. The page shows what is waiting now.");
        }

        PermissionChoice? choice = StringOf(root, "choice") switch
        {
            "allow" => PermissionChoice.Allow,
            "session" when pending.CanAllowForSession && pending is not QuestionItem => PermissionChoice.AllowForSession,
            "deny" => PermissionChoice.Deny,
            _ => null,
        };
        if (choice is not { } picked)
        {
            return PhoneResponse.Text(400, "That choice is not offered for this request.");
        }

        if (pending is QuestionItem question && picked != PermissionChoice.Deny)
        {
            if (ReadAnswers(question, root) is not { } answers)
            {
                return PhoneResponse.Text(400, "Every question needs an answer: pick an option or type one.");
            }

            ApplyAnswers(question, answers);
        }

        s.AnswerPending(picked);
        return pending.IsAnswered
            ? PhoneResponse.Text(200, "Answered.")
            : PhoneResponse.Text(409, s.Items.LastOrDefault() is ErrorItem error ? error.Message : "Nomad did not take that answer. Look at the chat on the PC.");
    }

    /// <summary>The chosen options and typed text for each question, or null unless every question gets a valid answer.</summary>
    private static List<(HashSet<QuestionOption> Options, string Other)>? ReadAnswers(QuestionItem question, JsonElement root)
    {
        if (!root.TryGetProperty("answers", out var answers) || answers.ValueKind != JsonValueKind.Array
            || answers.GetArrayLength() != question.Questions.Count)
        {
            return null;
        }

        var result = new List<(HashSet<QuestionOption>, string)>();
        var i = 0;
        foreach (var answer in answers.EnumerateArray())
        {
            var entry = question.Questions[i++];
            if (answer.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var picked = new HashSet<QuestionOption>();
            if (answer.TryGetProperty("options", out var labels))
            {
                if (labels.ValueKind != JsonValueKind.Array)
                {
                    return null;
                }

                foreach (var label in labels.EnumerateArray())
                {
                    if (label.ValueKind != JsonValueKind.String || entry.Options.FirstOrDefault(o => o.Label == label.GetString()) is not { } option)
                    {
                        return null;
                    }

                    picked.Add(option);
                }
            }

            var other = (StringOf(answer, "other") ?? "").Trim();
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

    // ------------------------------------------------------------------ helpers

    private static string? StringOf(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static byte[] Write(Action<Utf8JsonWriter> write)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var w = new Utf8JsonWriter(buffer))
        {
            write(w);
        }

        return buffer.WrittenSpan.ToArray();
    }

    private static JsonDocument? Parse(byte[] body)
    {
        try
        {
            return JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Clip(string text, int max = MaxText) => PhoneText.Clip(text, max);
}
