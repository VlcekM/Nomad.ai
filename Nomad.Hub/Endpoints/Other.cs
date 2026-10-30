using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Nomad.Hub.Contracts;
using Nomad.Hub.Data;
using Nomad.Hub.Services;

namespace Nomad.Hub;

public static class DeviceEndpoints
{
    public static readonly TimeSpan EnrollTokenLifetime = TimeSpan.FromHours(24);

    public static string Version { get; } =
        typeof(DeviceEndpoints).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0";

    /// <summary>Stores a new one-time enrollment token and returns it (only its hash is kept).</summary>
    public static async Task<EnrollTokenResponse> NewEnrollTokenAsync(HubDb db, Guid? createdBy, CancellationToken ct = default)
    {
        var token = Secrets.NewToken(18);
        var now = DateTime.UtcNow;
        db.EnrollTokens.Add(new EnrollToken { TokenHash = Secrets.Hash(token), CreatedUtc = now, ExpiresUtc = now + EnrollTokenLifetime, CreatedBy = createdBy });
        await db.SaveChangesAsync(ct);
        return new EnrollTokenResponse(token, now + EnrollTokenLifetime);
    }

    public static void MapAnonymous(IEndpointRouteBuilder app)
    {
        app.MapGet("/health", () => Results.Text("ok"));

        app.MapPost("/api/enroll", async (EnrollRequest request, HubDb db, CancellationToken ct) =>
        {
            var hash = Secrets.Hash(request.Token.Trim());
            var now = DateTime.UtcNow;
            var token = await db.EnrollTokens.SingleOrDefaultAsync(t => t.TokenHash == hash, ct);
            if (token is null || token.UsedUtc is not null || token.ExpiresUtc < now)
            {
                return Results.Unauthorized();
            }

            var secret = Secrets.NewToken();
            var device = new Device
            {
                Id = Guid.NewGuid(),
                Name = string.IsNullOrWhiteSpace(request.DeviceName) ? "PC" : request.DeviceName.Trim(),
                SecretHash = Secrets.Hash(secret),
                EnrolledUtc = now,
                LastSeenUtc = now,
            };
            token.UsedUtc = now;
            db.Devices.Add(device);
            await db.SaveChangesAsync(ct);
            return Results.Ok(new EnrollResponse(device.Id, secret, Version));
        });
    }

    public static void Map(RouteGroupBuilder api)
    {
        api.MapGet("/info", (HttpContext http, HubOptions options) =>
            new HubInfo(Version, http.User.DeviceId(), http.User.DeviceName(), options.RelayEnabled));

        api.MapGet("/devices", async (HttpContext http, HubDb db, Presence presence, CancellationToken ct) =>
        {
            var me = http.User.DeviceId();
            var devices = await db.Devices.AsNoTracking().Where(d => !d.Revoked).OrderBy(d => d.EnrolledUtc).ToListAsync(ct);
            return devices.Select(d => new DeviceDto(d.Id, d.Name, d.EnrolledUtc, d.LastSeenUtc, presence.IsOnline(d.Id), d.Id == me)).ToList();
        });

        api.MapDelete("/devices/{id:guid}", async (Guid id, HubDb db, CancellationToken ct) =>
        {
            var n = await db.Devices.Where(d => d.Id == id).ExecuteUpdateAsync(s => s.SetProperty(d => d.Revoked, true), ct);
            return n == 0 ? Results.NotFound() : Results.NoContent();
        });

        api.MapPost("/enroll-tokens", async (HttpContext http, HubDb db, CancellationToken ct) =>
            await NewEnrollTokenAsync(db, http.User.DeviceId(), ct));

        api.MapGet("/projects", async (HttpContext http, HubDb db, CancellationToken ct) =>
        {
            var me = http.User.DeviceId();
            return await db.Projects.AsNoTracking()
                .OrderBy(p => p.Name)
                .Select(p => new ProjectDto(p.Id, p.Key, p.Name,
                    db.ProjectFolders.Where(f => f.ProjectId == p.Id && f.DeviceId == me).Select(f => f.Path).FirstOrDefault()))
                .ToListAsync(ct);
        });

        api.MapPut("/projects/{id:guid}/folder", async (Guid id, ProjectFolderRequest request, HttpContext http, HubDb db, CancellationToken ct) =>
        {
            if (await db.Projects.FindAsync([id], ct) is null)
            {
                return Results.NotFound();
            }

            var me = http.User.DeviceId();
            var folder = await db.ProjectFolders.FindAsync([id, me], ct);
            if (folder is null)
            {
                db.ProjectFolders.Add(new ProjectFolder { ProjectId = id, DeviceId = me, Path = request.Path });
            }
            else
            {
                folder.Path = request.Path;
            }

            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });
    }
}

public static class BlobEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        api.MapMethods("/blobs/{hash}", ["HEAD"], (string hash, BlobStore blobs, HttpContext http) =>
        {
            if (blobs.LengthOf(hash) is not { } length)
            {
                return Results.NotFound();
            }

            http.Response.ContentLength = length;
            return Results.Ok();
        });

        api.MapGet("/blobs/{hash}", (string hash, BlobStore blobs) =>
            blobs.Exists(hash) ? Results.File(blobs.PathOf(hash), "application/octet-stream", enableRangeProcessing: true) : Results.NotFound());

        // The body is the whole file, or with ?base=<hash>&offset=<n>, what follows the base's first n bytes.
        api.MapPut("/blobs/{hash}", async (string hash, string? @base, long? offset, HttpRequest request, BlobStore blobs, CancellationToken ct) =>
        {
            if (!BlobStore.IsHash(hash) || (@base is not null && (!blobs.Exists(@base) || offset is null or < 0 || offset > blobs.LengthOf(@base))))
            {
                return Results.BadRequest();
            }

            var ok = await blobs.WriteAsync(hash, request.Body, @base, offset ?? 0, ct);
            return ok ? Results.NoContent() : Results.UnprocessableEntity("The content doesn't match its hash.");
        }).DisableRequestTimeout().WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(4L * 1024 * 1024 * 1024));
    }
}

public static class UsageEndpoints
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public static void Map(RouteGroupBuilder api)
    {
        api.MapPost("/usage", async (UsageBatch batch, HttpContext http, HubDb db, CancellationToken ct) =>
        {
            var device = http.User.DeviceId();
            await Gate.WaitAsync(ct);
            try
            {
                var sessionIds = batch.Records.Select(r => r.SessionId).Distinct().ToList();
                var existing = (await db.UsageRecords.AsNoTracking()
                        .Where(u => sessionIds.Contains(u.SessionId))
                        .Select(u => new { u.Backend, u.SessionId, u.MessageId })
                        .ToListAsync(ct))
                    .Select(u => (u.Backend, u.SessionId, u.MessageId))
                    .ToHashSet();
                var added = 0;
                foreach (var r in batch.Records)
                {
                    if (!existing.Add((r.Backend, r.SessionId, r.MessageId)))
                    {
                        continue;
                    }

                    db.UsageRecords.Add(new UsageRecord
                    {
                        Backend = r.Backend, SessionId = r.SessionId, MessageId = r.MessageId, Device = device,
                        Provider = r.Provider, Model = r.Model, AtUtc = r.AtUtc.ToUniversalTime(),
                        Input = r.Input, Output = r.Output, Reasoning = r.Reasoning, CacheRead = r.CacheRead, CacheWrite = r.CacheWrite,
                        ReportedUsd = r.ReportedUsd,
                    });
                    added++;
                }

                await db.SaveChangesAsync(ct);
                return new UsageBatchResult(batch.Records.Count, added);
            }
            finally
            {
                Gate.Release();
            }
        });

        api.MapGet("/usage", async (DateTime? from, DateTime? to, HttpContext http, HubDb db, Presence presence, CancellationToken ct) =>
        {
            var end = (to ?? DateTime.UtcNow).ToUniversalTime();
            var start = (from ?? end.AddDays(-31)).ToUniversalTime();
            var rows = await db.UsageRecords.AsNoTracking()
                .Where(u => u.AtUtc >= start && u.AtUtc < end)
                .GroupBy(u => new { u.AtUtc.Date, u.Backend, u.Model, u.Device })
                .Select(g => new
                {
                    g.Key.Date, g.Key.Backend, g.Key.Model, g.Key.Device,
                    Input = g.Sum(u => u.Input), Output = g.Sum(u => u.Output), Reasoning = g.Sum(u => u.Reasoning),
                    CacheRead = g.Sum(u => u.CacheRead), CacheWrite = g.Sum(u => u.CacheWrite),
                    Usd = g.Sum(u => u.ReportedUsd ?? 0), Calls = g.Count(),
                })
                .ToListAsync(ct);
            var me = http.User.DeviceId();
            var devices = await db.Devices.AsNoTracking().ToListAsync(ct);
            return new UsageReport(start, end,
                rows.Select(r => new UsageRow(DateOnly.FromDateTime(r.Date), r.Backend, r.Model, r.Device, r.Input, r.Output, r.Reasoning, r.CacheRead, r.CacheWrite, r.Usd, r.Calls)).ToList(),
                devices.Select(d => new DeviceDto(d.Id, d.Name, d.EnrolledUtc, d.LastSeenUtc, presence.IsOnline(d.Id), d.Id == me)).ToList());
        });
    }
}
