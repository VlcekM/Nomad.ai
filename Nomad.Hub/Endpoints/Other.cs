using System.Net;
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

    /// <summary>
    /// Stores a new one-time enrollment token and returns it (only its hash is kept). <paramref name="forViewer"/>: Add phone's,
    /// so the device it enrolls is a viewer.
    /// </summary>
    public static async Task<EnrollTokenResponse> NewEnrollTokenAsync(HubDb db, Guid? createdBy, CancellationToken ct = default, bool forViewer = false)
    {
        var token = Secrets.NewToken(18);
        var now = DateTime.UtcNow;
        db.EnrollTokens.Add(new EnrollToken
        {
            TokenHash = Secrets.Hash(token), CreatedUtc = now, ExpiresUtc = now + EnrollTokenLifetime, CreatedBy = createdBy, ForViewer = forViewer,
        });
        await db.SaveChangesAsync(ct);
        return new EnrollTokenResponse(token, now + EnrollTokenLifetime);
    }

    public static void MapAnonymous(IEndpointRouteBuilder app)
    {
        app.MapGet("/health", () => Results.Text("ok"));

        // One-time enrollment link (GET does not consume the token so link previews never burn it; POST /api/enroll consumes it).
        app.MapGet("/enroll/{token}", async (string token, HttpContext http, HubDb db, CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            http.Response.Headers["Referrer-Policy"] = "no-referrer";

            var trimmed = token.Trim();
            var hash = Secrets.Hash(trimmed);
            var now = DateTime.UtcNow;
            var record = await db.EnrollTokens.AsNoTracking().SingleOrDefaultAsync(t => t.TokenHash == hash, ct);
            if (record is null || record.UsedUtc is not null || record.ExpiresUtc < now)
            {
                return Results.Content(RenderExpiredEnrollPage(), "text/html; charset=utf-8", statusCode: StatusCodes.Status410Gone);
            }

            var hubUrl = PublicBaseUrl(http);
            return Results.Content(RenderEnrollPage(hubUrl, trimmed, record.ForViewer, record.ExpiresUtc), "text/html; charset=utf-8");
        });

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
                Name = string.IsNullOrWhiteSpace(request.DeviceName) ? token.ForViewer ? "Phone" : "PC" : request.DeviceName.Trim(),
                SecretHash = Secrets.Hash(secret),
                EnrolledUtc = now,
                LastSeenUtc = now,
                IsViewer = token.ForViewer,
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
            new HubInfo(Version, http.User.DeviceId(), http.User.DeviceName(), options.RelayEnabled)).WithMetadata(new ViewerAllowed());

        api.MapGet("/storage", async (HubDb db, HubOptions options, BlobStore blobs, PhoneFileStore phoneFiles, PhoneAppStore phoneApp, CancellationToken ct) =>
            await ComputeStorageAsync(db, options, blobs, phoneFiles, phoneApp, ct));

        api.MapGet("/devices", async (HttpContext http, HubDb db, Presence presence, CancellationToken ct) =>
        {
            var me = http.User.DeviceId();
            var devices = await db.Devices.AsNoTracking().Where(d => !d.Revoked).OrderBy(d => d.EnrolledUtc).ToListAsync(ct);
            return devices.Select(d => new DeviceDto(d.Id, d.Name, d.EnrolledUtc, d.LastSeenUtc, presence.IsOnline(d.Id), d.Id == me, d.IsViewer)).ToList();
        });

        // A PC revokes any device; a phone only itself (signing out).
        api.MapDelete("/devices/{id:guid}", async (Guid id, HttpContext http, HubDb db, LiveConnections live, CancellationToken ct) =>
        {
            if (http.User.IsViewer() && id != http.User.DeviceId())
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            var n = await db.Devices.Where(d => d.Id == id).ExecuteUpdateAsync(s => s.SetProperty(d => d.Revoked, true), ct);
            if (n > 0)
            {
                live.Abort(id); // Its open connections end too: a lost phone still connected is cut off now.
            }

            return n == 0 ? Results.NotFound() : Results.NoContent();
        }).WithMetadata(new ViewerAllowed());

        // ?phone=true: Add phone's token, which enrolls a viewer.
        api.MapPost("/enroll-tokens", async (HttpContext http, HubDb db, bool? phone, CancellationToken ct) =>
            await NewEnrollTokenAsync(db, http.User.DeviceId(), ct, phone == true));

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

    private static string PublicBaseUrl(HttpContext http)
    {
        var proto = http.Request.Headers["X-Forwarded-Proto"].FirstOrDefault()?.Split(',')[0].Trim();
        var scheme = !string.IsNullOrEmpty(proto) ? proto : http.Request.Scheme;
        var fwdHost = http.Request.Headers["X-Forwarded-Host"].FirstOrDefault()?.Split(',')[0].Trim();
        var host = !string.IsNullOrEmpty(fwdHost) ? fwdHost : http.Request.Host.Value;
        var pathBase = http.Request.PathBase.Value?.TrimEnd('/') ?? "";
        return $"{scheme}://{host}{pathBase}/";
    }

    private static string RenderExpiredEnrollPage() => """
        <!doctype html>
        <html lang="en">
        <head>
        <meta charset="utf-8" />
        <meta name="viewport" content="width=device-width,initial-scale=1" />
        <title>Nomad Hub - Enrollment link expired</title>
        <style>
          :root { color-scheme: dark light; --bg:#0f0f10; --card:#171718; --border:#262628; --text:#f0f0ee; --muted:#9a9a96; --accent:#009868; }
          @media (prefers-color-scheme: light) { :root { --bg:#f6f5f2; --card:#fff; --border:#e2e0da; --text:#181818; --muted:#666560; } }
          body { margin:0; font-family:system-ui,-apple-system,sans-serif; background:var(--bg); color:var(--text); display:grid; place-items:center; min-height:100vh; padding:20px; box-sizing:border-box; }
          .card { max-width:440px; width:100%; background:var(--card); border:1px solid var(--border); border-radius:14px; padding:28px; }
          h1 { margin:0 0 10px; font-size:20px; }
          p { margin:0; color:var(--muted); line-height:1.5; font-size:14px; }
        </style>
        </head>
        <body>
          <div class="card">
            <h1>Enrollment link expired</h1>
            <p>This one-time enrollment link has already been used or has expired. Generate a new link from Settings &rsaquo; Hub on an enrolled PC.</p>
          </div>
        </body>
        </html>
        """;

    private static string RenderEnrollPage(string hubUrl, string token, bool forViewer, DateTime expiresUtc)
    {
        var deepLink = $"nomad://enroll?hub={Uri.EscapeDataString(hubUrl)}&token={Uri.EscapeDataString(token)}";
        var hHub = WebUtility.HtmlEncode(hubUrl);
        var hToken = WebUtility.HtmlEncode(token);
        var hDeep = WebUtility.HtmlEncode(deepLink);
        var kind = forViewer ? "phone" : "PC";
        var hint = forViewer
            ? "Tap below to open Nomad on this phone, or enter the address and token manually in the app."
            : "Paste this page's link or the address and token below into Nomad's Settings &rsaquo; Hub on your PC.";
        var openBtn = forViewer
            ? $"<a class=\"btn\" href=\"{hDeep}\">Open in Nomad</a>"
            : "";
        var autoOpenScript = forViewer
            ? $"<script>setTimeout(function(){{window.location.href={System.Text.Json.JsonSerializer.Serialize(deepLink)};}},250);</script>"
            : "";

        return $$"""
        <!doctype html>
        <html lang="en">
        <head>
        <meta charset="utf-8" />
        <meta name="viewport" content="width=device-width,initial-scale=1" />
        <title>Connect to Nomad Hub</title>
        <style>
          :root { color-scheme: dark light; --bg:#0f0f10; --card:#171718; --field:#121213; --border:#262628; --text:#f0f0ee; --muted:#9a9a96; --accent:#009868; }
          @media (prefers-color-scheme: light) { :root { --bg:#f6f5f2; --card:#fff; --field:#f3f2ef; --border:#e2e0da; --text:#181818; --muted:#666560; } }
          body { margin:0; font-family:system-ui,-apple-system,sans-serif; background:var(--bg); color:var(--text); display:grid; place-items:center; min-height:100vh; padding:20px; box-sizing:border-box; }
          .card { max-width:460px; width:100%; background:var(--card); border:1px solid var(--border); border-radius:14px; padding:28px; box-sizing:border-box; }
          h1 { margin:0 0 8px; font-size:20px; }
          p { margin:0 0 18px; color:var(--muted); line-height:1.5; font-size:14px; }
          .btn { display:block; text-align:center; background:var(--accent); color:#fff; text-decoration:none; font-weight:600; padding:12px 16px; border-radius:9px; margin-bottom:20px; font-size:15px; }
          .label { font-size:12px; color:var(--muted); margin:12px 0 4px; }
          .row { display:flex; gap:8px; align-items:center; }
          code { flex:1; background:var(--field); border:1px solid var(--border); border-radius:8px; padding:9px 10px; font-size:13px; overflow-wrap:anywhere; font-family:ui-monospace,SFMono-Regular,Consolas,monospace; }
          button { background:var(--field); color:var(--text); border:1px solid var(--border); border-radius:8px; padding:9px 12px; font-size:13px; cursor:pointer; }
          .foot { margin-top:18px; font-size:12px; color:var(--muted); }
        </style>
        </head>
        <body>
          <div class="card">
            <h1>Connect this {{kind}} to Nomad Hub</h1>
            <p>{{hint}}</p>
            {{openBtn}}
            <div class="label">Hub address</div>
            <div class="row"><code id="hub">{{hHub}}</code><button onclick="cp('hub',this)">Copy</button></div>
            <div class="label">One-time enrollment token</div>
            <div class="row"><code id="tok">{{hToken}}</code><button onclick="cp('tok',this)">Copy</button></div>
            <div class="foot">Works once. Expires {{expiresUtc:u}}.</div>
          </div>
          <script>function cp(id,b){navigator.clipboard&&navigator.clipboard.writeText(document.getElementById(id).textContent).then(function(){b.textContent='Copied';setTimeout(function(){b.textContent='Copy';},1500);});}</script>
          {{autoOpenScript}}
        </body>
        </html>
        """;
    }

    private static async Task<HubStorageDto> ComputeStorageAsync(
        HubDb db, HubOptions options, BlobStore blobs, PhoneFileStore phoneFiles, PhoneAppStore phoneApp, CancellationToken ct)
    {
        var sessions = await db.Sessions.AsNoTracking()
            .Where(s => !s.Deleted)
            .Select(s => new { s.Id, s.Backend, s.Version })
            .ToListAsync(ct);

        var sessionById = sessions.ToDictionary(s => s.Id);

        // Map each blob hash referenced by a current session version to its backend.
        var currentRefs = await (
            from f in db.SessionFiles.AsNoTracking()
            join v in db.SessionVersions.AsNoTracking() on f.VersionId equals v.Id
            join s in db.Sessions.AsNoTracking() on v.SessionPk equals s.Id
            where !s.Deleted && v.Version == s.Version
            select new { f.Hash, s.Backend }
        ).ToListAsync(ct);

        var hashToBackend = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var r in currentRefs)
        {
            hashToBackend.TryAdd(r.Hash, r.Backend);
        }

        var backendBytes = new Dictionary<string, long>(StringComparer.Ordinal);
        long olderBlobBytes = 0;
        var olderBlobCount = 0;
        var totalBlobs = 0;

        foreach (var (hash, length) in blobs.AllSizes())
        {
            totalBlobs++;
            if (hashToBackend.TryGetValue(hash, out var backend))
            {
                backendBytes[backend] = backendBytes.GetValueOrDefault(backend) + length;
            }
            else
            {
                olderBlobBytes += length;
                olderBlobCount++;
            }
        }

        var sessionCountByBackend = sessions.GroupBy(s => s.Backend).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        var categories = new List<StorageCategoryDto>();

        foreach (var (backend, bytes) in backendBytes.OrderByDescending(kv => kv.Value))
        {
            if (bytes > 0)
            {
                categories.Add(new StorageCategoryDto(backend, BackendDisplayName(backend), bytes, sessionCountByBackend.GetValueOrDefault(backend)));
            }
        }

        if (olderBlobBytes > 0)
        {
            categories.Add(new StorageCategoryDto("older-versions", "Previous versions", olderBlobBytes, olderBlobCount));
        }

        // SQLite database + WAL/SHM.
        long dbBytes = 0;
        foreach (var name in new[] { "hub.db", "hub.db-wal", "hub.db-shm" })
        {
            var path = Path.Combine(options.DataPath, name);
            if (File.Exists(path))
            {
                dbBytes += new FileInfo(path).Length;
            }
        }

        if (dbBytes > 0)
        {
            categories.Add(new StorageCategoryDto("database", "Database", dbBytes, 1));
        }

        var (backupBytes, backupCount) = FolderSize(Path.Combine(options.DataPath, "backup"));
        if (backupBytes > 0)
        {
            categories.Add(new StorageCategoryDto("backups", "Backups", backupBytes, backupCount));
        }

        var (phoneFileBytes, phoneFileCount) = FolderSize(phoneFiles.Folder);
        if (phoneFileBytes > 0)
        {
            categories.Add(new StorageCategoryDto("phone-files", "Phone attachments", phoneFileBytes, phoneFileCount));
        }

        var (phoneAppBytes, phoneAppCount) = FolderSize(phoneApp.Folder);
        if (phoneAppBytes > 0)
        {
            categories.Add(new StorageCategoryDto("phone-app", "Phone app", phoneAppBytes, phoneAppCount));
        }

        var totalBytes = categories.Sum(c => c.Bytes);
        long? diskFree = null;
        long? diskTotal = null;
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(options.DataPath));
            if (!string.IsNullOrEmpty(root))
            {
                var drive = new DriveInfo(root);
                if (drive.IsReady)
                {
                    diskFree = drive.AvailableFreeSpace;
                    diskTotal = drive.TotalSize;
                }
            }
        }
        catch (Exception)
        {
            // Container mounts or virtual filesystems may not expose DriveInfo.
        }

        return new HubStorageDto(totalBytes, diskFree, diskTotal, sessions.Count, totalBlobs, categories);
    }

    private static (long Bytes, int Count) FolderSize(string path)
    {
        if (!Directory.Exists(path))
        {
            return (0, 0);
        }

        long bytes = 0;
        var count = 0;
        try
        {
            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                bytes += new FileInfo(file).Length;
                count++;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return (bytes, count);
    }

    private static string BackendDisplayName(string backend) => backend switch
    {
        "claude-code" => "Claude Code",
        "codex" => "Codex",
        "opencode" => "OpenCode",
        "antigravity" => "Antigravity",
        _ => backend,
    };
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
