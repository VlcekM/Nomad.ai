using Nomad.Hub.Contracts;
using Nomad.Hub.Services;

namespace Nomad.Hub;

/// <summary>
/// Files a phone attaches to a prompt (docs/mobile.md §3): put on the hub first, then named in the prompt, and fetched from here
/// by the PC that gets it. Each is kept in the data folder's <c>phone-files</c> folder, under its id, for a day.
/// </summary>
public sealed class PhoneFileStore(HubOptions options)
{
    /// <summary>The largest file a phone may attach.</summary>
    public const long MaxSize = 50L * 1024 * 1024;

    public static readonly TimeSpan KeepFor = TimeSpan.FromDays(1);

    // Whole, so a relative data folder is found from the working folder.
    public string Folder => Path.GetFullPath(Path.Combine(options.DataPath, "phone-files"));

    public static bool IsId(string id) => id.Length == 32 && id.All(char.IsAsciiHexDigitLower);

    /// <summary>Stores the file; null when it is larger than <see cref="MaxSize"/> (nothing is kept then).</summary>
    public async Task<ChatFile?> SaveAsync(Stream content, string name, CancellationToken ct)
    {
        var id = Guid.NewGuid().ToString("N");
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

        // For the PC that got the prompt (a phone can't read files back).
        api.MapGet("/files/{id}", (string id, PhoneFileStore store) => store.Find(id) is { } found
            ? Results.File(found.Path, "application/octet-stream", found.Name)
            : Results.NotFound());
    }
}
