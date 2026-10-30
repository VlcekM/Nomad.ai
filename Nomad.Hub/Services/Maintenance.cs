using Microsoft.EntityFrameworkCore;
using Nomad.Hub.Data;

namespace Nomad.Hub.Services;

/// <summary>
/// Hourly: drops session versions beyond <see cref="HubOptions.KeepVersions"/> and blobs nothing refers to (an hour
/// old at least, so an upload waiting for its commit stays). Nightly: a <c>VACUUM INTO</c> snapshot in <c>backup/</c>.
/// </summary>
public sealed class Maintenance(IServiceScopeFactory scopes, HubOptions options, BlobStore blobs, ILogger<Maintenance> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var lastBackup = DateOnly.MinValue;
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
        do
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<HubDb>();
                await SweepAsync(db, stoppingToken);
                var today = DateOnly.FromDateTime(DateTime.UtcNow);
                if (today != lastBackup)
                {
                    await BackupAsync(db, stoppingToken);
                    lastBackup = today;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogWarning(ex, "Maintenance failed");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    public async Task SweepAsync(HubDb db, CancellationToken ct)
    {
        var keep = Math.Max(1, options.KeepVersions);
        var old = await db.SessionVersions
            .Join(db.Sessions, v => v.SessionPk, s => s.Id, (v, s) => new { v, s.Version })
            .Where(x => x.v.Version <= x.Version - keep)
            .Select(x => x.v.Id)
            .ToListAsync(ct);
        if (old.Count > 0)
        {
            await db.SessionFiles.Where(f => old.Contains(f.VersionId)).ExecuteDeleteAsync(ct);
            await db.SessionVersions.Where(v => old.Contains(v.Id)).ExecuteDeleteAsync(ct);
        }

        var used = (await db.SessionFiles.Select(f => f.Hash).Distinct().ToListAsync(ct)).ToHashSet();
        var cutoff = DateTime.UtcNow.AddHours(-1);
        foreach (var (hash, written) in blobs.All().ToList())
        {
            if (!used.Contains(hash) && written < cutoff)
            {
                blobs.Delete(hash);
            }
        }
    }

    private async Task BackupAsync(HubDb db, CancellationToken ct)
    {
        var dir = Path.Combine(options.DataPath, "backup");
        Directory.CreateDirectory(dir);
        var target = Path.Combine(dir, $"hub-{DateTime.UtcNow:yyyyMMdd}.db");
        if (File.Exists(target))
        {
            return;
        }

        await db.Database.ExecuteSqlRawAsync("VACUUM INTO {0}", [target], ct);
        foreach (var stale in Directory.GetFiles(dir, "hub-*.db").OrderDescending().Skip(Math.Max(1, options.KeepBackups)))
        {
            File.Delete(stale);
        }
    }
}
