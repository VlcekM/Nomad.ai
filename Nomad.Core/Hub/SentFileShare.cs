using Nomad.Hub.Contracts;

namespace Nomad.Core.Hub;

/// <summary>
/// This PC's side of a phone fetching a file a chat sent the user (<see cref="HubNames.ChatFileRequested"/>): the file is checked
/// at once and named in the answer, then sent to the place the hub kept for it, away from the hub's calls, so they don't wait
/// behind the upload. A file sent before (the same path, size and last write) is named again rather than sent again, while the
/// hub still has it.
/// </summary>
/// <param name="upload">Fills a place on the hub: (the place's id, the file's content, its name, its size).</param>
public sealed class SentFileShare(Func<string, Stream, string, long, CancellationToken, Task<ChatFile>> upload, TimeProvider? time = null)
{
    /// <summary>The largest file a phone is sent (the hub's limit).</summary>
    public const long MaxSize = 50L * 1024 * 1024;

    /// <summary>How long a copy on the hub is named again: less than the day the hub keeps it.</summary>
    public static readonly TimeSpan ReuseFor = TimeSpan.FromHours(20);

    /// <summary>How long one upload may take.</summary>
    private static readonly TimeSpan UploadBudget = TimeSpan.FromMinutes(10);

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly Lock _lock = new();
    private readonly Dictionary<string, Sent> _sent = new(StringComparer.OrdinalIgnoreCase);

    private sealed record Sent(long Length, DateTime WrittenUtc, string Id, DateTime SentUtc, Task Upload);

    /// <summary>
    /// The file as the hub will have it: in the place <paramref name="slot"/> (sent from now on), or the copy put there before.
    /// Throws, with a message for the phone, when the file has gone or is too large.
    /// </summary>
    public ChatFile Offer(string path, string slot)
    {
        var info = new FileInfo(path);
        if (!info.Exists)
        {
            throw new InvalidOperationException("The file is no longer on the PC.");
        }

        if (info.Length > MaxSize)
        {
            throw new InvalidOperationException($"The file is too large for the phone ({MaxSize / (1024 * 1024)} MB at most); open it on the PC.");
        }

        var key = info.FullName;
        var name = info.Name;
        var length = info.Length;
        var written = info.LastWriteTimeUtc;
        var now = _time.GetUtcNow().UtcDateTime;
        lock (_lock)
        {
            if (_sent.TryGetValue(key, out var before) && before.Length == length && before.WrittenUtc == written && now - before.SentUtc < ReuseFor
                && !before.Upload.IsFaulted && !before.Upload.IsCanceled)
            {
                return new ChatFile(before.Id, name, length);
            }

            var sending = Task.Run(async () =>
            {
                using var budget = new CancellationTokenSource(UploadBudget);
                await using var content = new FileStream(key, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 81920, useAsync: true);
                await upload(slot, content, name, length, budget.Token).ConfigureAwait(false);
            });
            _sent[key] = new Sent(length, written, slot, now, sending);
            _ = sending.ContinueWith(failed =>
            {
                // Not there after all: the next ask sends it again. (The phone's fetch hears it from the hub.)
                _ = failed.Exception;
                lock (_lock)
                {
                    if (_sent.TryGetValue(key, out var entry) && entry.Id == slot)
                    {
                        _sent.Remove(key);
                    }
                }
            }, CancellationToken.None, TaskContinuationOptions.NotOnRanToCompletion, TaskScheduler.Default);
            return new ChatFile(slot, name, length);
        }
    }

    /// <summary>The upload into a place, while it is known (for tests).</summary>
    internal Task? UploadOf(string id)
    {
        lock (_lock)
        {
            return _sent.Values.FirstOrDefault(s => s.Id == id)?.Upload;
        }
    }
}
