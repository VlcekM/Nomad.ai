using System.Text.Json;
using Nomad.Core.Protocol;

namespace Nomad.Core.Sessions;

/// <summary>One displayable piece of a stored conversation.</summary>
public abstract record HistoryItem;

public sealed record HistoryUserMessage(string Text) : HistoryItem
{
    /// <summary>The transcript record's uuid (rewind and fork point at it).</summary>
    public string? Uuid { get; init; }

    /// <summary>The record before it: where a rewind to before this message resumes.</summary>
    public string? ParentUuid { get; init; }

    public DateTime? TimestampUtc { get; init; }

    /// <summary>The images sent with it, base64 as stored (decoded only when shown).</summary>
    public IReadOnlyList<HistoryImage> Images { get; init; } = [];
}

public sealed record HistoryImage(string MediaType, string Base64);

public sealed record HistoryAssistantText(string MessageId, string Text) : HistoryItem;

public sealed record HistoryThinking(string Text) : HistoryItem;

public sealed record HistoryToolCall(string ToolUseId, string Name, JsonElement Input) : HistoryItem;

public sealed record HistoryToolResult(string ToolUseId, bool IsError, string Text, JsonElement Structured) : HistoryItem;

/// <summary>Muted one-line notices: truncation, interruption, compaction.</summary>
public sealed record HistoryNotice(string Text) : HistoryItem;

/// <summary>A stored conversation, reconstructed along its parent chain.</summary>
public sealed class SessionHistory
{
    public required string SessionId { get; init; }
    public string? WorkingDirectory { get; init; }
    public string? Model { get; init; }
    public bool Truncated { get; init; }
    public required IReadOnlyList<HistoryItem> Items { get; init; }

    /// <summary>
    /// Builds the history from transcript records. Follows <c>parentUuid</c> (or
    /// <c>logicalParentUuid</c> across compactions) back from the last conversation record, so
    /// abandoned branches (rewinds, edits) are left out; falls back to file order when the chain is
    /// unusable.
    /// </summary>
    public static SessionHistory FromRecords(IReadOnlyList<TranscriptRecord> records, bool truncated, string fallbackSessionId)
    {
        var byUuid = new Dictionary<string, TranscriptRecord>(StringComparer.Ordinal);
        TranscriptRecord? leaf = null;
        string? cwd = null;
        string? sessionId = null;
        string? model = null;
        var conversationCount = 0;
        foreach (var r in records)
        {
            if (r.Uuid is not null)
            {
                byUuid[r.Uuid] = r;
            }

            cwd ??= r.Cwd;
            sessionId ??= r.SessionId;
            if (r.IsConversationRecord && r.IsSidechain != true)
            {
                leaf = r;
                conversationCount++;
                if (r.Type == "assistant" && !string.IsNullOrEmpty(r.Message?.Model) && r.Message.Model != "<synthetic>")
                {
                    model = r.Message.Model;
                }
            }
        }

        var chain = new List<TranscriptRecord>();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        for (var current = leaf; current is not null;)
        {
            if (current.Uuid is null || !visited.Add(current.Uuid))
            {
                break;
            }

            chain.Add(current);
            var next = current.ParentUuid ?? current.LogicalParentUuid;
            current = next is not null && byUuid.TryGetValue(next, out var parent) ? parent : null;
        }

        chain.Reverse();
        var chainConversation = chain.Count(r => r.IsConversationRecord);
        if (chainConversation < Math.Min(2, conversationCount) || chainConversation * 2 < conversationCount && truncated)
        {
            chain = records.Where(r => r.IsSidechain != true).ToList();
        }

        var items = new List<HistoryItem>();
        if (truncated)
        {
            items.Add(new HistoryNotice("Earlier history truncated"));
        }

        foreach (var record in chain)
        {
            AddItems(record, items);
        }

        return new SessionHistory
        {
            SessionId = sessionId ?? fallbackSessionId,
            WorkingDirectory = cwd,
            Model = model,
            Truncated = truncated,
            Items = RestoreFolderMoves(items),
        };
    }

    /// <summary>The CLI records our handoff as an interrupt; restore the move notice shown live instead.</summary>
    private static IReadOnlyList<HistoryItem> RestoreFolderMoves(List<HistoryItem> items)
    {
        const string continuationPrefix = "Continue the task in ";
        var calls = new HashSet<string>(StringComparer.Ordinal);
        var hidden = new HashSet<int>();
        var moves = new Dictionary<int, string>();
        for (var i = 0; i < items.Count; i++)
        {
            if (items[i] is HistoryToolCall { Name: Agents.FolderMoveTool.ToolName } call)
            {
                calls.Add(call.ToolUseId);
            }
            else if (items[i] is HistoryToolResult { IsError: false } result && calls.Contains(result.ToolUseId))
            {
                var next = i + 1;
                while (next < items.Count && items[next] is not HistoryUserMessage)
                {
                    next++;
                }

                if (next >= items.Count || items[next] is not HistoryUserMessage prompt ||
                    !prompt.Text.StartsWith(continuationPrefix, StringComparison.Ordinal) || !prompt.Text.EndsWith('.'))
                {
                    continue;
                }

                var folder = prompt.Text[continuationPrefix.Length..^1];
                if (!result.Text.Contains($"The user approved moving this conversation to {folder}.", StringComparison.Ordinal))
                {
                    continue;
                }

                moves[next] = folder;
                for (var j = i + 1; j < next; j++)
                {
                    if (items[j] is HistoryNotice { Text: "Interrupted" } or HistoryAssistantText { Text: "No response requested." })
                    {
                        hidden.Add(j);
                    }
                }
            }
        }

        if (moves.Count == 0)
        {
            return items;
        }

        var restored = new List<HistoryItem>(items.Count);
        for (var i = 0; i < items.Count; i++)
        {
            if (moves.TryGetValue(i, out var folder))
            {
                restored.Add(new HistoryNotice($"Moved to {folder}"));
            }

            if (!hidden.Contains(i))
            {
                restored.Add(items[i]);
            }
        }

        return restored;
    }

    private static void AddItems(TranscriptRecord record, List<HistoryItem> items)
    {
        if (record.IsSidechain == true)
        {
            return;
        }

        switch (record.Type)
        {
            // A background task's end, handed to Claude between turns.
            case "attachment" when record.Attachment.ValueKind == System.Text.Json.JsonValueKind.Object
                && record.Attachment.TryGetProperty("prompt", out var prompt) && prompt.ValueKind == System.Text.Json.JsonValueKind.String
                && TaskNotification.TryParse(prompt.GetString(), out var summary):
                items.Add(new HistoryNotice(summary));
                return;
            case "system" when record.Subtype == "compact_boundary":
                items.Add(new HistoryNotice("Conversation compacted"));
                return;
            case "user":
                AddUserItems(record, items);
                return;
            case "assistant":
                AddAssistantItems(record, items);
                return;
        }
    }

    private static void AddUserItems(TranscriptRecord record, List<HistoryItem> items)
    {
        if (record.IsMeta == true || record.IsCompactSummary == true || record.Message?.Content is not { } blocks)
        {
            return;
        }

        var texts = new List<string>();
        var images = new List<HistoryImage>();
        foreach (var block in blocks)
        {
            switch (block.Type)
            {
                case "tool_result" when block.ToolUseId is not null:
                    items.Add(new HistoryToolResult(block.ToolUseId, block.IsError == true, CliProtocol.ToolResultText(block.Content), record.ToolUseResult));
                    break;
                case "text" when !string.IsNullOrWhiteSpace(block.Text):
                    var trimmed = block.Text.TrimStart();
                    if (trimmed.StartsWith("[Request interrupted", StringComparison.Ordinal))
                    {
                        items.Add(new HistoryNotice("Interrupted"));
                    }
                    else if (LocalCommandEcho.TryParse(trimmed, out var output))
                    {
                        if (output is not null)
                        {
                            items.Add(new HistoryNotice(output));
                        }
                    }
                    else if (TaskNotification.TryParse(trimmed, out var summary))
                    {
                        items.Add(new HistoryNotice(summary));
                    }
                    else if (!trimmed.StartsWith('<'))
                    {
                        texts.Add(block.Text.Trim());
                    }

                    break;
                case "image" when block.Source.ValueKind == JsonValueKind.Object
                    && block.Source.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.String:
                    images.Add(new HistoryImage(
                        block.Source.TryGetProperty("media_type", out var type) && type.ValueKind == JsonValueKind.String ? type.GetString()! : "image/png",
                        data.GetString()!));
                    break;
            }
        }

        if (texts.Count > 0 || images.Count > 0)
        {
            items.Add(new HistoryUserMessage(string.Join("\n\n", texts))
            {
                Images = images,
                Uuid = record.Uuid,
                ParentUuid = record.ParentUuid ?? record.LogicalParentUuid,
                TimestampUtc = DateTime.TryParse(record.Timestamp, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var at) ? at : null,
            });
        }
    }

    private static void AddAssistantItems(TranscriptRecord record, List<HistoryItem> items)
    {
        if (record.Message?.Content is not { } blocks)
        {
            return;
        }

        var messageId = record.Message.Id ?? record.Uuid ?? "";
        foreach (var block in blocks)
        {
            switch (block.Type)
            {
                case "text" when !string.IsNullOrEmpty(block.Text):
                    items.Add(new HistoryAssistantText(messageId, block.Text));
                    break;
                case "thinking" when !string.IsNullOrWhiteSpace(block.Thinking):
                    items.Add(new HistoryThinking(block.Thinking));
                    break;
                case "tool_use" when block.Id is not null:
                    items.Add(new HistoryToolCall(block.Id, block.Name ?? "tool", block.Input));
                    break;
            }
        }
    }
}
