using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Nomad.Hub.Contracts;
using Nomad.Hub.Data;

namespace Nomad.Hub.Services;

public sealed class HubOptions
{
    /// <summary>Where <c>hub.db</c>, <c>blobs/</c> and <c>backup/</c> live (the Docker volume).</summary>
    public string DataPath { get; set; } = "/data";

    /// <summary>Phase 2's live relay; off, other PCs see snapshots only.</summary>
    public bool RelayEnabled { get; set; } = true;

    /// <summary>How many versions of each session the hub keeps (their blobs with them).</summary>
    public int KeepVersions { get; set; } = 3;

    /// <summary>Nightly database snapshots kept in <c>backup/</c>.</summary>
    public int KeepBackups { get; set; } = 7;
}

public static class Secrets
{
    public static string NewToken(int bytes = 32) =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(bytes)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static string Hash(string secret) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));
}

/// <summary>
/// The hub is one process on one SQLite file: writes go one at a time through this gate, which also numbers them
/// for the change feed and tells connected PCs once they are saved.
/// </summary>
public sealed class ChangeLog(IHubContext<NomadHub> hub)
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<T> WriteAsync<T>(HubDb db, Func<Func<Task<long>>, Task<T>> work, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        long? moved = null;
        T result;
        try
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            Counter? counter = null;
            result = await work(async () =>
            {
                counter ??= await db.Counters.SingleAsync(c => c.Name == Counter.Seq, ct);
                counter.Value++;
                return counter.Value;
            });
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            moved = counter?.Value;
        }
        finally
        {
            _gate.Release();
        }

        if (moved is { } seq)
        {
            await hub.Clients.All.SendAsync(HubNames.Changed, seq, CancellationToken.None);
        }

        return result;
    }

    public static async Task<long> CurrentAsync(HubDb db, CancellationToken ct = default) =>
        (await db.Counters.AsNoTracking().SingleAsync(c => c.Name == Counter.Seq, ct)).Value;
}

/// <summary>Which devices have a live connection, and how many.</summary>
public sealed class Presence
{
    private readonly ConcurrentDictionary<Guid, int> _connections = new();

    public void Connected(Guid device) => _connections.AddOrUpdate(device, 1, (_, n) => n + 1);

    public void Disconnected(Guid device) => _connections.AddOrUpdate(device, 0, (_, n) => Math.Max(0, n - 1));

    public bool IsOnline(Guid device) => _connections.TryGetValue(device, out var n) && n > 0;
}

/// <summary>Content-addressed files: <c>blobs/ab/abcdef…</c>, named by their SHA-256.</summary>
public sealed class BlobStore
{
    private readonly string _root;

    public BlobStore(HubOptions options)
    {
        _root = Path.Combine(options.DataPath, "blobs");
        Directory.CreateDirectory(_root);
    }

    public static bool IsHash(string hash) => hash.Length == 64 && hash.All(char.IsAsciiHexDigitLower);

    public string PathOf(string hash) => Path.Combine(_root, hash[..2], hash);

    public bool Exists(string hash) => IsHash(hash) && File.Exists(PathOf(hash));

    public long? LengthOf(string hash) => Exists(hash) ? new FileInfo(PathOf(hash)).Length : null;

    /// <summary>
    /// Stores <paramref name="content"/>, appended to the first <paramref name="baseLength"/> bytes of
    /// <paramref name="baseHash"/> when given, and keeps it only if the result hashes to <paramref name="hash"/>.
    /// </summary>
    public async Task<bool> WriteAsync(string hash, Stream content, string? baseHash, long baseLength, CancellationToken ct)
    {
        if (Exists(hash))
        {
            return true;
        }

        var temp = Path.Combine(_root, $".upload-{Guid.NewGuid():N}");
        try
        {
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                var buffer = new byte[81920];
                if (baseHash is not null)
                {
                    await using var source = File.OpenRead(PathOf(baseHash));
                    var left = baseLength;
                    while (left > 0)
                    {
                        var read = await source.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, left)), ct);
                        if (read == 0)
                        {
                            return false;
                        }

                        sha.AppendData(buffer, 0, read);
                        await file.WriteAsync(buffer.AsMemory(0, read), ct);
                        left -= read;
                    }
                }

                int n;
                while ((n = await content.ReadAsync(buffer, ct)) > 0)
                {
                    sha.AppendData(buffer, 0, n);
                    await file.WriteAsync(buffer.AsMemory(0, n), ct);
                }
            }

            if (Convert.ToHexStringLower(sha.GetHashAndReset()) != hash)
            {
                return false;
            }

            var target = PathOf(hash);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Move(temp, target, overwrite: true);
            return true;
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }

    /// <summary>Every stored hash with its last write time (for the sweep).</summary>
    public IEnumerable<(string Hash, DateTime WrittenUtc)> All() =>
        Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories)
            .Select(p => (Path.GetFileName(p), File.GetLastWriteTimeUtc(p)))
            .Where(x => IsHash(x.Item1));

    public void Delete(string hash)
    {
        if (IsHash(hash))
        {
            File.Delete(PathOf(hash));
        }
    }
}
