using Nomad.Hub.Contracts;
using Nomad.Hub.Services;

namespace Nomad.Hub;

/// <summary>
/// Files a phone attaches to a prompt (docs/mobile.md §3): put on the hub first, then named in the prompt, and fetched from here
/// by the PC that gets it. Also the files a chat sent the user, which its PC puts here for a phone to fetch, marked so: a phone
/// reads no other. Each is kept in the data folder's <c>phone-files</c> folder, under its id, for a day.
/// </summary>
public sealed class PhoneFileStore(HubOptions options)
{
    /// <summary>The largest file a phone may attach.</summary>
    public const long MaxSize = 50L * 1024 * 1024;

    public static readonly TimeSpan KeepFor = TimeSpan.FromDays(1);

    // Whole, so a relative data folder is found from the working folder.
    public string Folder => Path.GetFullPath(Path.Combine(options.DataPath, "phone-files"));

    public static bool IsId(string id) => id.Length == 32 && id.All(char.IsAsciiHexDigitLower);

    /// <summary>
    /// Stores the file, under a new id or the one given (a place kept by <see cref="SentFileSlots"/>); null when it is larger than
    /// <see cref="MaxSize"/> (nothing is kept then). <paramref name="forPhones"/>: a file sent to the user, which a phone may fetch.
    /// </summary>
    public async Task<ChatFile?> SaveAsync(Stream content, string name, CancellationToken ct, string? id = null, bool forPhones = false)
    {
        id ??= Guid.NewGuid().ToString("N");
        if (!IsId(id))
        {
            throw new ArgumentException("Not a file id.", nameof(id));
        }

        var dir = Path.Combine(Folder, id);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "content");
        long size = 0;
        try
        {
            await using (var file = File.Create(path))
            {
                var buffer = new byte[81920];
                int read;
                while ((read = await content.ReadAsync(buffer, ct)) > 0)
                {
                    size += read;
                    if (size > MaxSize)
                    {
                        throw new InvalidDataException();
                    }

                    await file.WriteAsync(buffer.AsMemory(0, read), ct);
                }
            }

            if (forPhones)
            {
                await File.WriteAllTextAsync(Path.Combine(dir, ForPhonesMark), "", ct);
            }

            // The name last: a file is found only once it is whole.
            await File.WriteAllTextAsync(Path.Combine(dir, "name"), name, ct);
            return new ChatFile(id, name, size);
        }
        catch (Exception ex)
        {
            Directory.Delete(dir, recursive: true);
            if (ex is InvalidDataException)
            {
                return null;
            }

            throw;
        }
    }

    /// <summary>The file's content and name, or null when there is none by that id (or it has expired).</summary>
    public (string Path, string Name)? Find(string id)
    {
        if (!IsId(id))
        {
            return null;
        }

        var dir = Path.Combine(Folder, id);
        var path = Path.Combine(dir, "content");
        var nameFile = Path.Combine(dir, "name");
        return File.Exists(path) && File.Exists(nameFile) ? (path, File.ReadAllText(nameFile)) : null;
    }

    private const string ForPhonesMark = "for-phones";

    /// <summary>Whether the file is one a chat sent the user, which a phone may fetch (not one a phone attached).</summary>
    public bool IsForPhones(string id) => IsId(id) && File.Exists(Path.Combine(Folder, id, ForPhonesMark));

    /// <summary>Removes the files kept longer than <see cref="KeepFor"/>.</summary>
    public void Sweep(DateTime nowUtc)
    {
        if (!Directory.Exists(Folder))
        {
            return;
        }

        foreach (var dir in Directory.GetDirectories(Folder))
        {
            if (Directory.GetCreationTimeUtc(dir) < nowUtc - KeepFor)
            {
                try
                {
                    Directory.Delete(dir, recursive: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Being read now, or locked: next time. The rest of the hourly sweep still runs.
                }
            }
        }
    }

    /// <summary>A name kept to what a file name may hold on any PC, without folders.</summary>
    public static string CleanName(string? name)
    {
        var clean = (name ?? "").Replace('\\', '/');
        clean = clean[(clean.LastIndexOf('/') + 1)..];
        clean = string.Concat(clean.Select(c => char.IsControl(c) || c is '<' or '>' or ':' or '"' or '|' or '?' or '*' ? '_' : c)).Trim().TrimEnd('.');
        if (clean.Length > 120)
        {
            var ext = Path.GetExtension(clean);
            clean = ext.Length is > 0 and <= 20 ? clean[..(120 - ext.Length)] + ext : clean[..120];
        }

        if (clean.Length == 0)
        {
            return "file";
        }

        // A name Windows keeps for a device (CON, NUL, COM1, with any extension) would never be made as a file on the PC.
        var stem = clean.Split('.')[0].TrimEnd().ToUpperInvariant();
        return stem is "CON" or "PRN" or "AUX" or "NUL" || (stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) && stem[3] is >= '1' and <= '9')
            ? "_" + clean
            : clean;
    }
}

/// <summary>
/// The places kept for files a chat sends the user (<see cref="HubNames.ChatFileGet"/>): the hub names one when a phone asks for a
/// file, the PC with the chat fills it with <c>PUT api/files/{id}</c>, and the phone's <c>GET</c> of it waits until it is filled.
/// Only the PC it was kept for may fill a place, once, and it must start within <see cref="Lifetime"/>. In memory only.
/// </summary>
public sealed class SentFileSlots
{
    /// <summary>How long a place waits for its PC to start sending the file.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(2);

    /// <summary>How long a phone's fetch waits for the file to arrive.</summary>
    public static readonly TimeSpan ArrivalWait = TimeSpan.FromMinutes(5);

    private readonly Lock _lock = new();
    private readonly Dictionary<string, Slot> _slots = new(StringComparer.Ordinal);

    private sealed class Slot(Guid pc)
    {
        public Guid Pc { get; } = pc;

        public bool Claimed { get; set; }

        public TaskCompletionSource<bool> Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>A new place for a file from <paramref name="pc"/>, dropped if the PC hasn't started sending it within the lifetime.</summary>
    public string Reserve(Guid pc, TimeSpan? lifetime = null)
    {
        var id = Guid.NewGuid().ToString("N");
        lock (_lock)
        {
            _slots[id] = new Slot(pc);
        }

        _ = Task.Delay(lifetime ?? Lifetime).ContinueWith(_ => Drop(id, unclaimedOnly: true), TaskScheduler.Default);
        return id;
    }

    /// <summary>Whether <paramref name="pc"/> may fill the place now: it was kept for it, and nobody has started filling it.</summary>
    public bool TryClaim(string id, Guid pc)
    {
        lock (_lock)
        {
            if (_slots.TryGetValue(id, out var slot) && slot.Pc == pc && !slot.Claimed)
            {
                slot.Claimed = true;
                return true;
            }

            return false;
        }
    }

    /// <summary>Whether the place is kept for <paramref name="pc"/> and its file hasn't arrived yet.</summary>
    public bool IsPendingFor(string id, Guid pc)
    {
        lock (_lock)
        {
            return _slots.TryGetValue(id, out var slot) && slot.Pc == pc;
        }
    }

    /// <summary>The place's file arrived (true) or never will (false): whoever waits for it is told.</summary>
    public void Finish(string id, bool arrived)
    {
        Slot? slot;
        lock (_lock)
        {
            _slots.Remove(id, out slot);
        }

        slot?.Done.TrySetResult(arrived);
    }

    /// <summary>A place not needed after all (the PC named a copy it had put here before).</summary>
    public void Drop(string id) => Drop(id, unclaimedOnly: false);

    private void Drop(string id, bool unclaimedOnly)
    {
        Slot? slot = null;
        lock (_lock)
        {
            if (_slots.TryGetValue(id, out var found) && !(unclaimedOnly && found.Claimed))
            {
                _slots.Remove(id);
                slot = found;
            }
        }

        slot?.Done.TrySetResult(false);
    }

    /// <summary>Whether the place's file arrives, once known; null when there is no such place (the file is there, or never was).</summary>
    public Task<bool>? Arrival(string id)
    {
        lock (_lock)
        {
            return _slots.TryGetValue(id, out var slot) ? slot.Done.Task : null;
        }
    }
}

public static class FileEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        // A phone's attachment: the body is the file.
        api.MapPost("/files", async (HttpRequest request, string? name, PhoneFileStore store, CancellationToken ct) =>
            {
                var tooLarge = $"A file can be {PhoneFileStore.MaxSize / (1024 * 1024)} MB at most.";
                if (request.ContentLength > PhoneFileStore.MaxSize)
                {
                    return Results.Problem(tooLarge, statusCode: StatusCodes.Status413PayloadTooLarge);
                }

                return await store.SaveAsync(request.Body, PhoneFileStore.CleanName(name), ct) is { } saved
                    ? Results.Ok(saved)
                    : Results.Problem(tooLarge, statusCode: StatusCodes.Status413PayloadTooLarge);
            })
            .WithMetadata(new ViewerAllowed());

        // A file a chat sent the user, from the PC with the chat, into the place the hub kept for it (phones get 403).
        api.MapPut("/files/{id}", async (string id, HttpRequest request, string? name, PhoneFileStore store, SentFileSlots slots, CancellationToken ct) =>
        {
            if (!PhoneFileStore.IsId(id) || !slots.TryClaim(id, request.HttpContext.User.DeviceId()))
            {
                return Results.NotFound();
            }

            ChatFile? saved = null;
            try
            {
                if (request.ContentLength is null or <= PhoneFileStore.MaxSize)
                {
                    saved = await store.SaveAsync(request.Body, PhoneFileStore.CleanName(name), ct, id, forPhones: true);
                }
            }
            finally
            {
                slots.Finish(id, saved is not null);
            }

            return saved is not null
                ? Results.Ok(saved)
                : Results.Problem($"A file can be {PhoneFileStore.MaxSize / (1024 * 1024)} MB at most.", statusCode: StatusCodes.Status413PayloadTooLarge);
        });

        // For the PC that got a phone's prompt. A phone may fetch only a file a chat sent the user, and waits for it to arrive.
        api.MapGet("/files/{id}", async (string id, HttpContext http, PhoneFileStore store, SentFileSlots slots, CancellationToken ct) =>
            {
                var phone = http.User.IsViewer();
                if (phone && slots.Arrival(id) is { } arrival)
                {
                    try
                    {
                        await arrival.WaitAsync(SentFileSlots.ArrivalWait, ct);
                    }
                    catch (TimeoutException)
                    {
                        return Results.Problem("The PC hasn't sent the file.", statusCode: StatusCodes.Status504GatewayTimeout);
                    }
                }

                if (store.Find(id) is not { } found)
                {
                    return Results.NotFound();
                }

                if (phone && !store.IsForPhones(id))
                {
                    return Results.StatusCode(StatusCodes.Status403Forbidden);
                }

                // Always a download, never a page: nothing in a file runs from here, whatever it holds.
                http.Response.Headers.XContentTypeOptions = "nosniff";
                return Results.File(found.Path, "application/octet-stream", found.Name);
            })
            .WithMetadata(new ViewerAllowed());
    }
}
