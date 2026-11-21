using System.Buffers;
using System.Net.Sockets;
using System.Text;
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
    private const int MaxText = 20_000;

    private PhoneHttpServer? _server;

    /// <summary>The one phone view of this window, set at startup.</summary>
    public static PhoneView? Current { get; set; }

    /// <summary>"Serving on …", why it could not start, or "Off".</summary>
    public string Status { get; private set; } = "Off";

    public bool IsRunning => _server is not null;

    /// <summary>Raised on the UI thread after <see cref="Apply"/> changed <see cref="Status"/>.</summary>
    public event Action? StatusChanged;

    /// <summary>Starts, restarts or stops the server to match the settings (a new port or key restarts it).</summary>
    public void Apply()
    {
        _server?.Dispose();
        _server = null;
        var s = main.Settings;
        if (!s.PhoneView)
        {
            Status = "Off";
        }
        else
        {
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

        StatusChanged?.Invoke();
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
        var id = request.Query.TryGetValue("id", out var value) ? value : "";
        return (request.Method, request.Path) switch
        {
            ("GET", "/") => PhoneResponse.Html(PhoneViewPage.Html),
            ("GET", "/api/chats") => PhoneResponse.Json(await OnUiAsync(ListChats)),
            ("GET", "/api/chat") => await OnUiAsync(() => Chat(id)),
            ("POST", "/api/send") => await OnUiTaskAsync(() => SendAsync(id, request.Body)),
            ("POST", "/api/answer") => await OnUiAsync(() => Answer(id, request.Body)),
            _ => PhoneResponse.Text(404, "Not found"),
        };
    }

    private static Task<T> OnUiAsync<T>(Func<T> work) => Dispatcher.UIThread.InvokeAsync(work).GetTask();

    private static Task<T> OnUiTaskAsync<T>(Func<Task<T>> work) => Dispatcher.UIThread.InvokeAsync(work);

    /// <summary>The chats the phone can see: opened ones, not the phone sessions Claude's own app drives.</summary>
    private IEnumerable<SessionViewModel> Chats() => main.OpenChats.Where(s => !s.IsNew && !s.IsPhoneSession);

    private SessionViewModel? Find(string id) => Chats().FirstOrDefault(s => s.ChatKey == id);

    private static string State(SessionViewModel s) =>
        s.PendingPermission is { IsAnswered: false } ? "waiting" : s.IsTurnRunning ? "working" : s.IsProcessRunning ? "idle" : "stopped";

    private byte[] ListChats() => Write(w =>
    {
        w.WriteStartArray();
        foreach (var s in Chats())
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

    private PhoneResponse Chat(string id)
    {
        if (Find(id) is not { } s)
        {
            return PhoneResponse.Text(404, "That chat is no longer open in Nomad.");
        }

        return PhoneResponse.Json(Write(w =>
        {
            w.WriteStartObject();
            w.WriteString("id", s.ChatKey);
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
                w.WriteBoolean("pending", pending && !q.IsAnswered);
                w.WriteString("answer", q.AnswerText);
                w.WriteStartArray("questions");
                foreach (var entry in q.Questions)
                {
                    w.WriteStartObject();
                    w.WriteString("question", entry.Question);
                    w.WriteString("header", entry.Header);
                    w.WriteBoolean("multi", entry.MultiSelect);
                    w.WriteStartArray("options");
                    foreach (var option in entry.Options)
                    {
                        w.WriteStartObject();
                        w.WriteString("label", option.Label);
                        w.WriteString("description", option.Description ?? "");
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
                w.WriteString("title", p.Title);
                w.WriteString("detail", Clip(p.Detail, 4000));
                w.WriteString("description", p.Description ?? "");
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

    private async Task<PhoneResponse> SendAsync(string id, byte[] body)
    {
        if (Find(id) is not { } s)
        {
            return PhoneResponse.Text(404, "That chat is no longer open in Nomad.");
        }

        var text = ReadString(body, "text")?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return PhoneResponse.Text(400, "Nothing to send.");
        }

        var busy = s.IsTurnRunning;
        await s.ReceiveMessageAsync(text);
        return PhoneResponse.Text(200, busy ? "Queued: it is working, and gets this when the turn ends." : "Sent.");
    }

    /// <summary>
    /// Answers the chat's pending card: <c>{"choice": "allow" | "session" | "deny"}</c>, and for a question card
    /// <c>"answers": [{"options": [labels], "other": "text"}, …]</c> in the order of its questions.
    /// </summary>
    private PhoneResponse Answer(string id, byte[] body)
    {
        if (Find(id) is not { } s)
        {
            return PhoneResponse.Text(404, "That chat is no longer open in Nomad.");
        }

        if (s.PendingPermission is not { IsAnswered: false } pending)
        {
            return PhoneResponse.Text(409, "Nothing is waiting for an answer any more.");
        }

        using var doc = Parse(body);
        if (doc is null)
        {
            return PhoneResponse.Text(400, "Not JSON.");
        }

        var root = doc.RootElement;
        var choice = (root.TryGetProperty("choice", out var c) ? c.GetString() : null) switch
        {
            "allow" => PermissionChoice.Allow,
            "session" => PermissionChoice.AllowForSession,
            "deny" => PermissionChoice.Deny,
            _ => (PermissionChoice?)null,
        };
        if (choice is not { } picked)
        {
            return PhoneResponse.Text(400, "choice must be allow, session or deny.");
        }

        if (pending is QuestionItem question && picked != PermissionChoice.Deny)
        {
            if (!root.TryGetProperty("answers", out var answers) || answers.ValueKind != JsonValueKind.Array)
            {
                return PhoneResponse.Text(400, "A question needs its answers.");
            }

            var i = 0;
            foreach (var answer in answers.EnumerateArray())
            {
                if (i >= question.Questions.Count)
                {
                    break;
                }

                var entry = question.Questions[i++];
                foreach (var option in entry.Options)
                {
                    option.IsSelected = false;
                }

                if (answer.TryGetProperty("options", out var labels) && labels.ValueKind == JsonValueKind.Array)
                {
                    foreach (var label in labels.EnumerateArray())
                    {
                        if (entry.Options.FirstOrDefault(o => o.Label == label.GetString()) is { } option)
                        {
                            entry.Pick(option);
                        }
                    }
                }

                entry.OtherText = answer.TryGetProperty("other", out var other) ? other.GetString() ?? "" : "";
            }

            if (!question.Questions.All(q => q.IsAnswered))
            {
                return PhoneResponse.Text(400, "Every question needs an answer.");
            }
        }

        s.AnswerPending(picked);
        return PhoneResponse.Text(200, "Answered.");
    }

    // ------------------------------------------------------------------ helpers

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

    private static string? ReadString(byte[] body, string name)
    {
        using var doc = Parse(body);
        return doc is { RootElement.ValueKind: JsonValueKind.Object } && doc.RootElement.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;
    }

    private static string Clip(string text, int max = MaxText) => text.Length <= max ? text : text[..max] + "…";
}
