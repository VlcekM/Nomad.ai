using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Nomad.Hub.Contracts;
using Nomad.Hub.Data;
using Nomad.Hub.Services;

namespace Nomad.Hub;

public static class Sessions
{
    public static readonly string[] Backends = ["claude-code", "codex", "opencode"];

    public static async Task<Session> FindOrCreateAsync(HubDb db, string backend, string sessionId)
    {
        var session = db.Sessions.Local.FirstOrDefault(s => s.Backend == backend && s.SessionId == sessionId)
            ?? await db.Sessions.SingleOrDefaultAsync(s => s.Backend == backend && s.SessionId == sessionId);
        if (session is null)
        {
            // A placeholder (version 0, no files): state or a lease can come before the first upload.
            session = new Session { Backend = backend, SessionId = sessionId, UpdatedUtc = DateTime.UtcNow };
            db.Sessions.Add(session);
            await db.SaveChangesAsync();
        }

        return session;
    }

    /// <summary>The DTOs of these sessions, with their current version's files and project key.</summary>
    public static async Task<List<SessionDto>> ToDtosAsync(HubDb db, IReadOnlyList<Session> sessions, CancellationToken ct)
    {
        var ids = sessions.Select(s => s.Id).ToList();
        var files = await db.SessionVersions.AsNoTracking()
            .Where(v => ids.Contains(v.SessionPk))
            .Join(db.Sessions, v => v.SessionPk, s => s.Id, (v, s) => new { v, s.Version })
            .Where(x => x.v.Version == x.Version)
            .Select(x => new { x.v.SessionPk, Files = x.v.Files.Select(f => new SessionFileDto(f.Path, f.Hash, f.Length)).ToList() })
            .ToListAsync(ct);
        var filesBySession = files.ToDictionary(f => f.SessionPk, f => (IReadOnlyList<SessionFileDto>)f.Files);

        var projectIds = sessions.Where(s => s.ProjectId is not null).Select(s => s.ProjectId!.Value).Distinct().ToList();
        var keys = await db.Projects.AsNoTracking().Where(p => projectIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.Key, ct);

        return sessions.Select(s => new SessionDto(
            s.Backend, s.SessionId, s.ProjectId, s.ProjectId is { } p ? keys.GetValueOrDefault(p) : null,
            s.Title, s.WorkingDirectory, s.GitBranch, s.LastActivityUtc, s.Size, s.Version,
            filesBySession.GetValueOrDefault(s.Id) ?? [], s.UpdatedBy, s.Seq, s.Deleted,
            s.LeaseExpiresUtc > DateTime.UtcNow ? s.LeaseDevice : null,
            s.LeaseExpiresUtc > DateTime.UtcNow ? s.LeaseExpiresUtc : null)).ToList();
    }

    public static void Map(RouteGroupBuilder api)
    {
        var session = api.MapGroup("/sessions/{backend}/{sessionId}");

        session.MapGet("", async (string backend, string sessionId, HubDb db, CancellationToken ct) =>
        {
            var s = await db.Sessions.AsNoTracking().SingleOrDefaultAsync(x => x.Backend == backend && x.SessionId == sessionId, ct);
            return s is null ? Results.NotFound() : Results.Ok((await ToDtosAsync(db, [s], ct))[0]);
        });

        session.MapPut("", async (string backend, string sessionId, SessionCommit commit, HttpContext http, HubDb db, ChangeLog changes, BlobStore blobs, CancellationToken ct) =>
        {
            if (!Backends.Contains(backend))
            {
                return Results.BadRequest($"Unknown backend '{backend}'.");
            }

            var missing = commit.Files.Where(f => blobs.LengthOf(f.Hash) != f.Length).Select(f => f.Hash).Distinct().ToList();
            if (missing.Count > 0)
            {
                return Results.UnprocessableEntity(new CommitRejection("missing-blobs", null, missing));
            }

            var device = http.User.DeviceId();
            var outcome = await changes.WriteAsync(db, async nextSeq =>
            {
                var s = await FindOrCreateAsync(db, backend, sessionId);
                var now = DateTime.UtcNow;
                string? refusal =
                    s.Deleted ? "deleted"
                    : s.Version != commit.BaseVersion ? "version"
                    : s.LeaseDevice is { } holder && holder != device && s.LeaseExpiresUtc > now ? "leased"
                    : null;
                if (refusal is not null)
                {
                    return (Session: s, Refusal: refusal);
                }

                if (!string.IsNullOrWhiteSpace(commit.ProjectKey))
                {
                    var project = await db.Projects.SingleOrDefaultAsync(p => p.Key == commit.ProjectKey, ct);
                    if (project is null)
                    {
                        project = new Project { Id = Guid.NewGuid(), Key = commit.ProjectKey, Name = commit.ProjectName ?? commit.ProjectKey, CreatedUtc = now };
                        db.Projects.Add(project);
                    }

                    s.ProjectId = project.Id;
                    if (!string.IsNullOrWhiteSpace(commit.WorkingDirectory))
                    {
                        var folder = await db.ProjectFolders.FindAsync([project.Id, device], ct);
                        if (folder is null)
                        {
                            db.ProjectFolders.Add(new ProjectFolder { ProjectId = project.Id, DeviceId = device, Path = commit.WorkingDirectory });
                        }
                    }
                }

                s.Version++;
                db.SessionVersions.Add(new SessionVersion
                {
                    SessionPk = s.Id,
                    Version = s.Version,
                    Device = device,
                    CreatedUtc = now,
                    Files = commit.Files.Select(f => new SessionFile { Path = f.Path, Hash = f.Hash, Length = f.Length }).ToList(),
                });
                s.Title = commit.Title;
                s.WorkingDirectory = commit.WorkingDirectory;
                s.GitBranch = commit.GitBranch;
                s.LastActivityUtc = commit.LastActivityUtc;
                s.Size = commit.Files.Sum(f => f.Length);
                s.UpdatedBy = device;
                s.UpdatedUtc = now;
                s.Seq = await nextSeq();
                return (Session: s, Refusal: (string?)null);
            }, ct);

            var dto = (await ToDtosAsync(db, [outcome.Session], ct))[0];
            return outcome.Refusal is null ? Results.Ok(dto) : Results.Conflict(new CommitRejection(outcome.Refusal, dto, []));
        });

        session.MapDelete("", async (string backend, string sessionId, HubDb db, ChangeLog changes, CancellationToken ct) =>
        {
            await changes.WriteAsync(db, async nextSeq =>
            {
                var s = await FindOrCreateAsync(db, backend, sessionId);
                s.Deleted = true;
                s.LeaseDevice = null;
                s.LeaseExpiresUtc = null;
                s.Seq = await nextSeq();
                return true;
            }, ct);
            return Results.NoContent();
        });

        session.MapPut("/state/{key}", async (string backend, string sessionId, string key, SessionStateRequest request, HttpContext http, HubDb db, ChangeLog changes, CancellationToken ct) =>
        {
            var applied = await changes.WriteAsync(db, async nextSeq =>
            {
                var s = await FindOrCreateAsync(db, backend, sessionId);
                var state = await db.SessionStates.FindAsync([s.Id, key], ct);
                if (state is not null && state.SetUtc >= request.SetUtc)
                {
                    return false; // An older write: last writer wins.
                }

                if (state is null)
                {
                    state = new SessionState { SessionPk = s.Id, Key = key };
                    db.SessionStates.Add(state);
                }

                state.Value = request.Value;
                state.SetUtc = request.SetUtc;
                state.Device = http.User.DeviceId();
                state.Seq = await nextSeq();
                return true;
            }, ct);
            return applied ? Results.NoContent() : Results.StatusCode(StatusCodes.Status208AlreadyReported);
        });

        session.MapPost("/lease", async (string backend, string sessionId, HttpContext http, HubDb db, ChangeLog changes, CancellationToken ct) =>
        {
            var device = http.User.DeviceId();
            var lost = await Leases.RenewAsync(db, changes, device, [new LeaseKey(backend, sessionId)]);
            var s = await db.Sessions.AsNoTracking().SingleAsync(x => x.Backend == backend && x.SessionId == sessionId, ct);
            var holderName = s.LeaseDevice is { } h ? (await db.Devices.FindAsync([h], ct))?.Name : null;
            return Results.Ok(new LeaseDto(backend, sessionId, lost.Count == 0, s.LeaseDevice, holderName, s.LeaseExpiresUtc));
        });

        session.MapDelete("/lease", async (string backend, string sessionId, HttpContext http, HubDb db, ChangeLog changes, CancellationToken ct) =>
        {
            var device = http.User.DeviceId();
            await changes.WriteAsync(db, async nextSeq =>
            {
                var s = await FindOrCreateAsync(db, backend, sessionId);
                if (s.LeaseDevice == device)
                {
                    s.LeaseDevice = null;
                    s.LeaseExpiresUtc = null;
                    s.Seq = await nextSeq();
                }

                return true;
            }, ct);
            return Results.NoContent();
        });

        session.MapPost("/handoff", async (string backend, string sessionId, HttpContext http, HubDb db, Presence presence,
            IHubContext<NomadHub> hub, CancellationToken ct) =>
        {
            var s = await db.Sessions.AsNoTracking().SingleOrDefaultAsync(x => x.Backend == backend && x.SessionId == sessionId, ct);
            if (s?.LeaseDevice is not { } holder || s.LeaseExpiresUtc <= DateTime.UtcNow)
            {
                return Results.NoContent(); // Nobody holds it: take the lease.
            }

            if (holder == http.User.DeviceId())
            {
                return Results.NoContent();
            }

            if (!presence.IsOnline(holder))
            {
                return Results.Conflict(new LeaseDto(backend, sessionId, false, holder, (await db.Devices.FindAsync([holder], ct))?.Name, s.LeaseExpiresUtc));
            }

            await hub.Clients.Group(NomadHub.DeviceGroup(holder)).SendAsync(HubNames.HandoffRequested,
                new HandoffRequest(backend, sessionId, http.User.DeviceId(), http.User.DeviceName()), ct);
            return Results.Accepted();
        });

        api.MapGet("/changes", async (long? since, HubDb db, CancellationToken ct) =>
        {
            var from = since ?? 0;
            var seq = await ChangeLog.CurrentAsync(db, ct);
            var sessions = await db.Sessions.AsNoTracking().Where(s => s.Seq > from && s.Seq <= seq).OrderBy(s => s.Seq).ToListAsync(ct);
            var states = await db.SessionStates.AsNoTracking()
                .Where(x => x.Seq > from && x.Seq <= seq)
                .Join(db.Sessions, x => x.SessionPk, s => s.Id, (x, s) => new SessionStateDto(s.Backend, s.SessionId, x.Key, x.Value, x.SetUtc, x.Seq))
                .ToListAsync(ct);
            return Results.Ok(new ChangesDto(seq, await ToDtosAsync(db, sessions, ct), states));
        });
    }
}
