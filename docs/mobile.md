# Nomad Mobile - design

Status: the trial build (§7), Antigravity sync (§8) and the hub's board (§3, and §4 except Add phone) are built; the app is next.

Nomad Mobile is a phone app that shows every chat open in Nomad, on every one of the user's PCs, in one list, and lets the user read, answer and prompt them from anywhere. It targets Android first and is built with Avalonia, like the desktop app. The app talks only to Nomad Hub, never to a PC directly, so it works wherever the hub is reachable and no PC has to accept connections from outside.

## 1. What the first version does

- Lists the chats open in Nomad on every connected PC, for every backend (Claude Code, Codex, OpenCode, Antigravity). Each row shows the PC, folder, title, state (waiting on you, working, idle, stopped) and the last line said. Chats waiting on an answer sort first.
- Opens a chat: its recent transcript, then the running turn as it streams.
- Sends a message. During a running turn it queues, as a message typed on the PC does; to a chat whose process has stopped, it starts the chat again on its PC.
- Answers permission and question cards. The first answer from anywhere wins and the card closes everywhere, as the relay already does between PCs.
- Stops a running turn.

Left for later: notifications while the app is closed, starting new chats, browsing closed and archived sessions, changing model or mode, sending images and files (received ones show as placeholders), and iOS.

## 2. Why the hub, and what it is missing

The relay (hub design, §8) already lets one PC watch a session another PC runs, prompt it, answer its cards and stop it. A phone is one more watcher. Three things stand between the relay and a phone app:

1. The relay works one session at a time. Nothing tells a watcher which chats exist across PCs, or what state each is in, short of syncing every file.
2. The relay carries only the turn running now. A watching PC gets the earlier transcript from the synced session file and parses it itself. A phone should not carry every backend's file parsers, and for Antigravity it cannot: Antigravity's history is read only by running its CLI and replaying `session/load`, and syncing them to the hub (§8) moves whole databases, not a transcript a phone could read.
3. Input reaches a session only while its host streams it. A chat whose process has ended has no host to receive a prompt.

So the PC that has the chat open does all the backend-specific work and hands the phone backend-neutral data, and the hub routes between them. That one choice covers every backend, Antigravity included, without the phone needing anything synced. Syncing Antigravity chats between PCs is wanted for its own sake, and is covered separately in §8.

## 3. Hub additions

### Viewer devices

A phone enrolls like a PC, with an enrollment token, and is listed and revoked in Settings › Hub the same way. It needs no kind of its own: it simply never syncs, so it never takes a lease, never uploads, and is never asked to hand a session over. The board and the chat calls need the hub's relay on, as watching a session already does.

### The chat board

Each Nomad sends `BoardPublish(List<BoardChat>)` over its existing connection when its open chats change (it checks once a second, and sends only a change). The update carries the whole list, which is small. A `BoardChat` holds the backend, session id, title, agent name, folder, state, what waits on the user if anything (a few words), the last line (clipped to 200 characters), the last activity time and the number of queued prompts. The hub adds the device id and name. A PC that stops sharing (the relay off in its settings) sends an empty list once, and answers no requests for pages or input.

The hub keeps the board in memory, as it keeps relay buffers. When a PC disconnects, its chats leave the board, which is correct, because nothing could reach them. A PC briefly connected twice (a reconnect while the old connection is not yet timed out) shows once, with the chats of both, and requests go to the connection that published last. Every entry carries a sequence number that grows with each change, so a watcher keeps the newest. A viewer calls `BoardWatch()`, which returns the current board and joins the `board` group. Every change after that arrives as `BoardChanged`.

### History on demand

`ChatHistory(device, backend, sessionId, before, limit)` asks the PC that has the chat open for a page of its transcript. The hub forwards the request as a SignalR client result (`Clients.Client(connection).InvokeAsync<ChatPage>`) and waits up to 15 seconds. A page holds up to 200 entries (the phone asks for fewer), newest last, and `before` is the cursor for scrolling back: an entry's index is its item's position in the transcript. Positions shift when a transcript reloads or is rewound, so a page carries a generation number that changes then, and the phone starts again from the newest page; it also says while the transcript is still loading. Each entry's text is clipped to 12,000 characters, well inside the hub's existing 16 MB message limit.

### Routed input

`ChatSend(device, backend, sessionId, ChatInput)` delivers a prompt, an answer or Stop to the PC that has the chat open. That PC replies whether it took it, with a sentence saying what happened. An input may carry an id: a retry with the same id (after a timeout, say) is answered with the first result and applied once. Stop is answered at once, as the turn stops. The phone sends every input this way, whether or not the chat is streaming, so a stopped chat works the same as a live one. The relay is used only for what it does best: live events, through the existing `RelayWatch`.

All new wire types go in `Nomad.Hub.Contracts`, with source-generated JSON, so the desktop app stays NativeAOT.

## 4. The PC side

A `MobileHost` class in `Nomad.App`, beside `HubViewModel`, answers for the chats this PC has open.

- The board is built from `MainViewModel.ReachableChats`: open chats that have a session id and were not started from the Claude app. It is the same set the chat tools (`list_chats`, `send_to_chat`) reach.
- History is mapped from the transcript items the chat window already shows: user messages, assistant text, tool groups, cards, notices and errors. The window has already turned every backend into those items, so Antigravity needs no extra work and nothing is read from disk again.
- Every input is applied on the UI thread under the same checks the desktop uses. An answer must name the pending card's `RequestId`, or it is refused. A question's answers are all checked before any of them is applied. "Allow for this session" is accepted only where the PC itself offers it. A prompt goes through the chat's own queue, which starts the process if it has stopped.
- Live events already stream for every chat this PC starts while the hub relay is on (`AttachRelay` runs at every process start). Whether Antigravity's events stream correctly gets confirmed during the build.
- Settings › Hub gains **Add phone**. It makes an enrollment token and shows a QR code of `nomad://enroll?hub=<address>&token=<token>`, with the link as text beside it.

## 5. The app

### Projects

| Project | What it holds |
|---|---|
| `Nomad.Mobile` | A `net11.0` library with the views, view models and transcript model, and no Android types. |
| `Nomad.Mobile.Android` | The `net11.0-android` head: the activity, secure storage and the `nomad://` link handler. |
| `Nomad.Mobile.Tests` | View models tested against a fake hub client, plus Avalonia headless rendering tests. |

The app references `Nomad.Hub.Contracts` and `Nomad.Core`, for the hub connection, `RelayCodec`, the `AgentEvent` types and `PermissionDecision`. It calls only the platform-neutral parts of `Nomad.Core`; data protection and process hosting are never reached on a phone. If `Nomad.Core` proves too heavy for the APK (the Android app package), its hub client splits into a small library of its own. That gets decided after the first build, not before.

### Screens

1. **Connect.** The phone's camera app scans the QR code, and Android hands the `nomad://enroll` link to Nomad, so the app needs no camera code of its own. The address and token can also be pasted in. The device secret is kept in encrypted storage backed by the Android Keystore.
2. **Chats.** Grouped by PC. Chats waiting on you come first, then working ones, then the rest by last activity. The list updates live, and pulling down refreshes it.
3. **Chat.** The transcript is rendered as Markdown with Markdig. Tool groups are folded to their name and one line, and long output is clipped with "Show more". Below it sits the turn streaming now, then any card with its buttons or question options, then the message box, which stays above the keyboard. A Stop button shows while the chat is working.
4. **Settings.** The hub address, the PCs on the board, and Sign out, which also revokes this device on the hub.

The app takes Nomad's colors and fonts, and follows the phone's light or dark setting. Motion follows the desktop's rules: short eases on things that appear, fold or move, and nothing that draws attention to itself.

### Coming back from the background

Android may close the connection while the app is in the background. On return the app reconnects: SignalR reconnects dropped connections itself, and the app reconnects after a deliberate close, as the desktop does. It then calls `BoardWatch` again, re-watches the open chat (`WatchResult` returns the turn so far) and fetches the newest history page. The first version keeps no transcript on the phone, so nothing it shows can go stale.

### Connection security

The hub speaks plain HTTP, and Android refuses plain HTTP by default, so the app requires HTTPS. There are two ways to provide it. One is the reverse proxy the hub design already expects (§9). The other is Tailscale, whose `tailscale serve --https` gets a real certificate for the PC's private network name at no cost. The README covers both.

## 6. Security

- A phone holds a device secret, as a PC does. A viewer can do what a watching PC can (prompt, answer, stop) and nothing more: no files, no new chats, no commands.
- The PC checks every input as if it were typed there (§4).
- The secret is stored at rest under the Android Keystore.
- A lost phone is revoked from Settings › Hub on any PC; the hub ends its open connections at once, and its secret no longer signs in.

## 7. Building and testing

The build needs the .NET 11 SDK with the Android workload, plus the Android SDK and a JDK (Java Development Kit), which the workload's `InstallAndroidDependencies` target fetches.

The first step is a spike (a throwaway trial build): an empty Avalonia app on .NET 11 and Avalonia 11.3, built, installed and opened on a real Android phone, connecting to a hub over SignalR. If that does not hold, work stops so the maintainer can choose between Avalonia 12 and a `net10.0-android` head before anything else is built.

| Where | What is tested |
|---|---|
| `Nomad.Hub.Tests` | Two PCs and a viewer. The board appears, updates, and drops a PC that disconnects. History pages correctly. A routed prompt reaches a stopped chat. An answer to a stale card is refused. A revoked viewer is refused. |
| `Nomad.Mobile.Tests` | Chat ordering, answering cards, sending during a turn, and re-watching after a reconnect, all against a fake hub. |
| A real phone | Over mobile data through the hub, with screenshots for the pull request taken on the phone. |

For release, the workflow gains an Android job that puts a signed APK on each release page. The signing key is the maintainer's and is kept as a repository secret. Publishing on Google Play is optional and can come later.

## 8. Syncing Antigravity chats

Separately from the phone, the hub syncs Antigravity chats between PCs, as it already does Claude Code, Codex and OpenCode ones (hub design, §4, which describes how). Antigravity keeps each chat as its own SQLite database. A chat travels as a snapshot taken with SQLite's backup API, so a chat that was only read never counts as changed, and one changed on two PCs is set aside whole on the PC whose upload is refused, never lost.

## 9. Delivery

The first step is the trial build (§7), which is not a pull request.

| # | Pull request | Contains |
|---|---|---|
| 1 | Hub: sync Antigravity chats | §8 with its tests. |
| 2 | Hub: board, history and routed input | §3 and §4 with their tests: a PC and a phone through a real hub, and a live run of a dev Nomad driven by a stand-in phone, so this is testable before any app exists. |
| 3 | Nomad Mobile for Android | §5, Add phone in Settings, `Nomad.Mobile.Tests`, screenshots, and the phone view's removal. |
| 4 | Android release build | The workflow job and signing. |

After these: notifications (they need either a foreground service or Firebase Cloud Messaging, which is the maintainer's choice), iOS (which needs a Mac to build and an Apple developer account), closed sessions, new chats from the phone, and images.

## 10. The phone view

The phone view (Settings › Remote Control) is a page Nomad serves itself. The app replaces it, and it is removed in the app's pull request. Its checks carry over to the PC side in §4: refusing an answer to a card that is no longer showing, checking a question's answers whole, and skipping unchanged polls.

## 11. Settled with the maintainer

- The hub is the only way in. The app never connects to a PC directly.
- The app replaces the phone view.
- The first version is Android only.
- Antigravity chats sync through the hub, as part of this work.

Still open:

1. What package name and signing key should the app use, and should it go on Google Play?
2. What is the minimum Android version? This plan proposes Android 10 (API level 29).
