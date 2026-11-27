using System.Text.Json;
using System.Text.Json.Nodes;

namespace Nomad.Core.Agents;

/// <summary>
/// Which kept copy each <c>send_file</c> call in a chat made, by the call's id in the agent's transcript, so a transcript loaded later
/// shows exactly the file that call sent (a saved transcript may not keep the call's result: Antigravity keeps none). One small file
/// per chat, in Nomad's kept folder; read once per chat and then answered from memory.
/// </summary>
public static class SentFileIndex
{
    /// <summary>
    /// Serialises the reads and writes.
    /// </summary>
    private static readonly Lock Gate = new();

    /// <summary>
    /// The indexes read so far, by folder and chat.
    /// </summary>
    private static readonly Dictionary<string, Dictionary<string, string>> Loaded = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The folder the indexes are in.
    /// </summary>
    public static string FolderPath => Path.Combine(AgentTools.KeptRoot, "sent-index");

    /// <summary>
    /// The kept copy the call <paramref name="toolCallId"/> in the chat <paramref name="sessionId"/> sent; null when none is recorded.
    /// </summary>
    public static string? Find(string sessionId, string toolCallId)
    {
        lock (Gate)
        {
            return Index(sessionId).GetValueOrDefault(toolCallId);
        }
    }

    /// <summary>
    /// Records that the call <paramref name="toolCallId"/> in the chat <paramref name="sessionId"/> sent <paramref name="kept"/>.
    /// </summary>
    public static void Record(string sessionId, string toolCallId, string kept)
    {
        lock (Gate)
        {
            var index = Index(sessionId);
            if (index.TryGetValue(toolCallId, out var known) && known == kept) return;

            index[toolCallId] = kept;
            try
            {
                Directory.CreateDirectory(FolderPath);
                var path = PathOf(sessionId);
                var temporary = path + ".tmp";
                var json = new JsonObject();
                foreach (var (call, copy) in index) json[call] = copy;
                File.WriteAllText(temporary, json.ToJsonString());
                File.Move(temporary, path, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Kept in memory for this session; a later record writes it again.
            }
        }
    }

    /// <summary>
    /// The chat's index, read from its file the first time.
    /// </summary>
    private static Dictionary<string, string> Index(string sessionId)
    {
        var path = PathOf(sessionId);
        if (Loaded.TryGetValue(path, out var index)) return index;

        index = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            if (File.Exists(path) && JsonNode.Parse(File.ReadAllText(path)) is JsonObject saved)
            {
                foreach (var (call, kept) in saved)
                {
                    if (kept is JsonValue value && value.TryGetValue<string>(out var text)) index[call] = text;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Unreadable: the chat's files just don't show again.
        }

        Loaded[path] = index;
        return index;
    }

    /// <summary>
    /// The chat's index file.
    /// </summary>
    private static string PathOf(string sessionId)
    {
        var name = string.Concat(sessionId.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        return Path.Combine(FolderPath, name + ".json");
    }
}
