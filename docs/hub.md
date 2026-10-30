# Nomad Hub — design

Status: design only, nothing built yet.

A small self-hosted server that keeps several workstations' Nomad in step: the same sessions on every PC,
one place for token use from every provider, and (optionally) a session running on one PC driven live from
another. Single user, no tenancy. Nomad works exactly as today without it.

## 1. Scope and phases

| Phase | What | Needs relay |
|---|---|---|
| 1 | Session sync (transcripts and Nomad's per-session state), host leases, usage records | No |
| 2 | Live relay: watch and drive a session another PC is hosting | Yes |

The relay is optional on both ends: the hub has `Relay__Enabled` (default `true`), and each Nomad has
**Stream live sessions to the hub** (Settings → Hub, default off). Without it, a session hosted elsewhere
shows its last synced snapshot, read-only, with a **Live on \<device\>** mark and one action, **Continue here**
(a handoff, §5).

Stats are shown in Nomad only. The hub has no web UI; it serves an API.

## 2. Hub stack

- `Nomad.Hub`: ASP.NET Core on .NET 10 (move to 11 when it ships), Minimal APIs (or FastEndpoints), JIT, not AOT,
  published `PublishReadyToRun`, no trimming.
- EF Core with the SQLite provider; normal `dotnet ef migrations add`; `Database.Migrate()` at start, before Kestrel listens.
- Session files are content-addressed blobs on disk (`/data/blobs/ab/cdef…`); the database holds metadata only.
- Wire DTOs in `Nomad.Hub.Contracts`, shared with the app, with source-generated JSON so the app stays NativeAOT.

## 3. Projects and folders

Claude Code files sessions under the encoded working folder (`~/.claude/projects/R--VlcekM-Nomad-ai`), and the
same repository sits in different folders on different PCs. So the hub keys a **project** by something that
does not depend on the path:

1. The git remote `origin` URL, normalised (`github.com/vlcekm/nomad.ai`).
2. Without a remote: the first commit's hash.
3. Not a git folder: the folder's name plus the device it came from; the other PCs map it by hand the first time.

`ProjectFolders` keeps device × project → local path. A session from a project this PC has no folder for is
listed under its project with **Choose folder…**; once a folder is chosen it is remembered for the whole project.
Fetched transcripts are written under the local folder's encoded project directory, so `--resume` finds them where
it always does. Scratch ("No folder") sessions map to this PC's scratch folder.

## 4. What syncs

| Backend | Files | Notes |
|---|---|---|
| Claude Code | `<project>/<session>.jsonl`, `<project>/<session>/subagents/**` (subagent and workflow transcripts) | Rename's `custom-title` records come along in the file |
| Codex | `~/.codex/sessions/YYYY/MM/DD/rollout-*-<id>.jsonl` (and `archived_sessions`) | Resumed with `thread/resume`; check whether Codex keeps an index beside the files |
| OpenCode | Sessions live in `opencode.db`, not files | Moved with `opencode export <id>` / `opencode import <file>` if those round-trip; otherwise view-only elsewhere (open question) |

**Nomad's per-session state**, from `NomadSettings`, syncs last-writer-wins per key with a timestamp:
`ArchivedSessions`, `PinnedSessions`, `UnarchivedUtc`, `MovedSessionFolders` (rewritten through the folder map),
`InterruptedSessions`. **Local only:** window and panel sizes, collapsed groups, recent/pinned folders, fonts,
theme, CLI paths, phone folders, Remote Control bridges.

Never synced: `~/.claude.json`, CLI settings and sign-ins, MCP config, `.codex/auth.json`.

## 5. Hosts and leases

Every session has at most one **host**: the PC whose Nomad runs its CLI process, and the only one that writes
its files. Nothing is ever merged.

- Before starting or resuming a CLI process, Nomad takes the session's **lease** from the hub. The host renews it
  every 30 s; it lapses after 2 min without renewal (the host crashed or went offline).
- A lease held by another PC: the session opens as a viewer (live with the relay, snapshot without it).
- **Continue here** (handoff): the viewer asks; the host ends its process, pushes the final file and releases the
  lease; the new PC takes the lease, pulls, resumes. A lapsed lease can be taken over at once, with a warning
  that the last turn on the old PC may not have synced.
- **Offline fallback:** when the hub can't be reached, Nomad runs sessions as today, without a lease. If two PCs
  then both extended the same session, the upload whose start doesn't match the hub's copy is stored as a
  **separate session** (a new id, titled "… (from \<device\>)") and flagged. Never merged, never overwritten.

Claude Code's own Remote Control is unaffected: the host can keep it on, so the phone, claude.ai/code and other
PCs (through the relay) all drive the same process.

## 6. Transfer

- Transcripts only grow, mostly. An upload says the length and hash of the prefix the hub already has; if they
  match, only the tail is sent (`PATCH` append), otherwise the whole file. Rewind (which cuts a transcript)
  takes the whole-file path.
- The host uploads after each turn and on lease release; other PCs learn of changes from the change feed (§8) and
  fetch lazily: listing needs only the summary the hub stores (title, folder, branch, last activity, size); a file
  is pulled when the session is opened or resumed.
- Blobs stream through the API, gzip on the wire, no size limit in the database. A blob is dropped when no
  session version refers to it; the hub keeps the last 3 versions of each session.

## 7. Usage records

Each Nomad extracts usage from what it already reads and uploads it; the hub stores and aggregates.
`Nomad.Core/Usage/LocalTokenUsage.cs` already reads Claude Code and Codex totals for this PC's month (the
new-session cards); the hub version extends it to per-message records with model and cache split, and the cards
then show all PCs' totals when a hub is set.

| Backend | Source | Note |
|---|---|---|
| Claude Code | `message.usage` on assistant records (input, output, cache read, cache write) | Streamed chunks repeat the same usage: one record per `message.id` |
| Codex | `token_count` events in rollouts (live: `thread/tokenUsage/updated`) | Running totals: store the differences |
| OpenCode | Each assistant message's tokens and cost (what `OpenCodeCost` reads) | Comes with OpenCode's own USD cost |

- Key `(backend, sessionId, messageId)`: uploads are idempotent, so a retry, a crash or the **backfill** (all
  local history, once, when a PC enrolls) never counts twice.
- Columns: device, backend, provider, model, time (UTC), input, output, reasoning, cache read, cache write, and
  `reportedUsd` when the provider gives one.
- Cost for plans (Claude, ChatGPT) is "API-equivalent", worked out in Nomad at display time from a price table,
  so a price fix applies to history too. OpenCode's is real spend.
- Optional: `PlanUsage` snapshots (session and weekly %) every few hours, for a plan-use-over-time line.
- Shown on Nomad's home (the new-session usage cards): this week/month by provider and model, per device on hover.

## 8. Transport

- HTTP for everything that isn't live: enrollment, files, state, usage, the change feed.
- One persistent connection per Nomad for presence, lease renewals, change notifications and (phase 2) the relay.
  The SignalR .NET client has supported NativeAOT since .NET 9 (JSON protocol, a `JsonSerializerContext`); confirm
  with a spike in `Nomad.App` before committing to it. Fallback: a plain WebSocket carrying JSON lines.
- **Change feed:** `GET /api/changes?since=<seq>` returns what changed (sessions, state, leases) after a
  monotonic sequence number. The connection just says "seq moved"; a PC that was offline catches up with one call.

### Relay (phase 2)

- The host sends its session's normalised `AgentEvent`s (what all three backends already turn into) to the hub,
  which fans them out to the session's viewers and keeps the current turn's events, so a viewer that joins
  mid-turn sees the whole turn.
- Viewers send prompts, permission and question answers, Stop and model/mode changes; the hub forwards them to the
  host, which applies them as if typed locally. One user, so any viewer may answer a permission prompt; the first
  answer wins and the card closes everywhere.
- Prompts typed on a viewer carry a device mark in the transcript, as phone prompts do.

## 9. Auth

- At first start the hub prints a one-time **enrollment token** (and `nomad-hub enroll` makes a new one).
  Settings → Hub takes the hub URL and the token; the hub answers with a device id and a long-lived device
  secret, kept by Nomad with DPAPI.
- Requests carry `Authorization: Bearer <device secret>` (stored hashed). Devices are listed and revoked in Settings → Hub.
- The hub speaks plain HTTP; TLS comes from the user's reverse proxy or Cloudflare Tunnel.

## 10. Deployment

```yaml
services:
  hub:
    image: vlcekmato/nomad-hub:latest
    restart: unless-stopped
    ports: ["127.0.0.1:8090:8080"]
    volumes: ["./data:/data"]
    environment:
      Relay__Enabled: "true"
      Sessions__KeepVersions: "3"
```

Image `mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled`, non-root, `/data` holding `hub.db` and `blobs/`.
Backup: `VACUUM INTO` (the hub does it nightly into `/data/backup/`) plus `blobs/`; fits Backrest as is.
Built and pushed by a GitHub Actions workflow next to the app's release workflow.

## 11. Data model

| Table | Holds |
|---|---|
| `Devices` | Name, secret hash, enrolled / last seen, revoked |
| `Projects` | Key (remote URL / root commit / name), display name |
| `ProjectFolders` | Device × project → local path |
| `Sessions` | Backend, id, project, title, branch, last activity, size, current version, change seq |
| `SessionVersions` | Session, blob hash, length, prefix hash, uploaded by / at |
| `Blobs` | Hash, size, refcount |
| `Leases` | Session, device, taken / renewed at |
| `SessionState` | Session, key (archived, pinned, …), value, set by / at |
| `UsageRecords` | (backend, session, message) key, device, provider, model, time, token columns, reported USD |

## 12. API

| Endpoint | |
|---|---|
| `POST /api/enroll` | Token → device id and secret |
| `GET /api/changes?since=` | Change feed |
| `GET /api/sessions` | Summaries (filtered by project, backend) |
| `GET /api/sessions/{id}/file` | Current transcript (and subagent files as a tar) |
| `PUT /api/sessions/{id}/file` / `PATCH` | Whole file / append a tail |
| `PUT /api/sessions/{id}/state/{key}` | Archived, pinned, … |
| `POST /api/sessions/{id}/lease` / `DELETE` | Take or renew / release |
| `POST /api/sessions/{id}/handoff` | Ask the host to hand over |
| `GET/PUT /api/projects`, `/api/projects/{id}/folder` | Projects and this device's folder for them |
| `POST /api/usage` | Batch of usage records (idempotent) |
| `GET /api/usage?from&to&groupBy=day\|provider\|model\|device` | Aggregates for the home card |
| `GET/DELETE /api/devices` | List / revoke devices |
| `/hubs/nomad` | Presence, leases, change pings, relay |

## 13. Open questions

1. **OpenCode:** do `opencode export` / `import` round-trip a session so it resumes on another PC? If not,
   OpenCode sessions are view-only elsewhere.
2. **Lease timing:** 30 s renewal, 2 min expiry, fine?
3. **Codex:** does it keep a thread index (state database) beside the rollout files that an imported rollout must be added to?
4. **Sync everything or opt in:** every session by default, or only projects ticked in Settings → Hub?
5. **Retention:** keep archived sessions on the hub forever, or drop them after N days?
