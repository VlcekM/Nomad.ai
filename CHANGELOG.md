# Changelog

Released with tags `v<version>` by `.github/workflows/release.yml`, which needs a non-empty `## v<version>` section here.
Newest first: the newest heading is also the version of local builds (`Directory.Build.props`).

## v1.12.0

- Sessions can be pinned by dragging: a "Drop to pin" zone opens under New while a session is dragged (a pinned one
  is unpinned there).
- Files dragged over a session show a "Drop to attach" frame: images and files dropped anywhere on it are attached.
- Alive and Idle scroll together in one list, headings and all; the Alive list is no longer capped in height.

## v1.11.0

- Sessions can be pinned in the sidebar: they sit first under a Pinned heading, never end for being idle and are never
  auto-archived.
- Sidebar multi-select: Ctrl/Shift+click picks several sessions, and the row menu archives or deletes them all.
- Project cards can be removed from Recent projects (right-click); they come back with the next session there.
- Pinned project cards show a filled, brighter pin.
- The Alive list has its own options menu: group by, and a Show project switch for a second line with the project.
- Path links open on a plain click; Ctrl+click brings Explorer to the front. The title bar shows the project before the
  branch.
- Background task ends show as a notice in the transcript again.
- The session list is kept between runs, so the app starts faster; placeholder rows show while things load.
- Settings: bigger switches that glide, and the dialog and its pages fade in smoothly.
- The app icon's tent sits centred in its tile.
- Plan usage: "Couldn't refresh" gets a retry button that says why it still can't; reset times show only on hover,
  and the per-model weekly row (Fable) drops its reset and forecast.

## v1.10.0

- The branch chip also shows how many files have uncommitted changes; the branch, ↑N and changes are re-read every 5
  seconds.
- Reopened chats show their images again; emoji are drawn in color.
- Ctrl+T search opens at once, even with many sessions.
- Remote Control sessions started from Nomad.ai get their generated title on claude.ai instead of "Untitled session".
- A setting hides the usage ring.
- Holding Ctrl shows the sessions' Ctrl+number badges at once.
- The Idle heading shows a moon instead of a count and folds the list when clicked anywhere; idle titles are muted.
- Links react on their whole line; file links show a hand cursor.
- The thin "Side panel" strip on the right edge is gone (Ctrl+Shift+B and the pane icon still toggle the panel).
- A Background tasks card in the side panel lists background commands: what runs, for how long, how it ended.
- Sessions with a subagent or background command still at work no longer go idle after an hour; background subagents
  now show as finished when they end.

## v1.9.0

- The branch chip (and a started session's branch in the title bar) shows ↑N when the branch has commits not pushed
  yet.
- Clicking a notification opens its session, also when Nomad.ai is already running.
- Send instantly is on Ctrl: Ctrl+Enter or Ctrl+click sends at once while Claude works, and holding Ctrl slides the
  label into the send button.
- Alive sessions in a worktree go Idle once its branch is merged.
- Mode moved from the status row into the model menu, as a nested Mode menu.
- Git chips slide and fade into place; the Worktree toggle has a label and no white flash on hover.
- The Idle heading drops its New button; its list options fade in on hover.
- The account bar at the bottom left is slimmer, with more room around it.

## v1.8.0

- Ctrl+T searches sessions by title and conversation, with Alive / Idle / Archived chips to filter by; ages show up to
  30 days, then the date.
- Sidebar rows of sessions whose worktree branch has been merged show a white merged icon at the left.
- Sessions whose turn was cut short by Nomad.ai closing (a restart, an update, a crash) carry on by themselves at the
  next start, told "You were interrupted by an app restart. Continue".
- New app icon: a black tent on a white rounded square.
- Context menus look like the popup menus; the session row tooltip fits its text and the path no longer wraps.
- Pane icons fill while their pane is shown; the side panel's cards have a menu.
- Shift+Enter sends; Esc no longer stops a turn.
- Paths in inline code: Ctrl+click works on every line of a wrapped path and opens Explorer.

## v1.7.0

- Sidebar rows show the session's GitHub pull request and issue in their state's colours (open, draft, merged,
  closed; open, done, not planned), from its branch, the links it mentions or its title; a click opens them. Read
  with the `gh` CLI and your own sign-in.
- Title bar: click the title to rename the session in place; a started session's branch and worktree sit right of it.
  On a new session they stay under the composer, and glide between the two places.
- Rename boxes (title bar and sidebar) are plain text with a caret, no frame.
- The side panel can be resized from its left edge; both resize edges show a thin blue line on hover and while
  dragging. The right-edge strip says "Side panel" (sideways), hidden while it is open.
- The side panel stays closed on the New page and slides in once the session starts.
- Files Claude sends (SendUserFile) show in the transcript: images large, other files as chips that open on a click.
- Question card: rounded option highlights with some room around them, and a white Submit button.

## v1.6.0

- Light theme: Settings › Appearance › Theme (Dark, Light or System); it switches at once.
- Project cards: pin a project to the top (the pin on hover, or right-click); the cards are larger.
- Git chip next to the folder: the branch (on a new session, click to switch it) and a Worktree switch: a new
  session then starts in a new git worktree (`.claude\worktrees`) on its own branch, from the branch picked.
- Chat titles are generated as the Claude app does it: "Untitled session" until the title arrives, and a rename in
  the meantime wins.
- Notifications and a flashing taskbar button when a session finishes or needs an answer while Nomad.ai is in the
  background; each can be turned off (Settings › Window).
- Copy image: right-click a pasted or sent image, or the button (Ctrl+C) in the large view.
- The status-row ring shows the weekly plan limit, its tooltip when it resets; it is a little larger.
- Context counts the context in use, not every call of the turn added up.
- Subagents show their model and running time, with what they are doing beneath.
- The mouse's back and forward buttons go back and forward between sessions.
- Tooltips sit above their control and close as soon as the pointer leaves it.
- New sessions start in the default permission mode again.

## v1.5.0

- Side panel: the session's subagents with their duration and what they did last; a click opens a subagent's own
  conversation (Esc goes back). Background (async) agents run until their completion notice.
- Paths and URLs in `inline code`: a URL opens with a click, a path that exists with Ctrl+click (Explorer, with it
  selected); right-click either for Show in Explorer, Open and Copy.

- Sidebar: "Hours, then days" grouping (last 2, 8 and 24 hours, then each day); idle sessions show a sleeping moon,
  and long titles fade out instead of ending in "…".
- The transcript fades out under the title bar when scrolled.
- The side panel opens and closes from a strip on the window's right edge too; its text is a point larger.
- Composer image thumbnails have clean rounded corners.

## v1.4.1

- Rename a session: double-click its row, right-click › Rename, or F2 for the current one.

## v1.4.0

- New session: the latest projects as cards with their branch, changed files, last commits and last session; a click works there.
- What you typed on the New page stays while you open other sessions, and its text across restarts.
- Side panel (Ctrl+Shift+B or the title bar button): the folder's git state and commits, context, plan usage and MCP servers.
- Plan usage: smaller text; weekly bars show the even pace behind them and when the limit runs out at this week's rate.
  The last answer is shared between windows and shown, marked stale, when the endpoint refuses.
- Composer: attachments sit above a dashed rule with their names; click an image to see it large (← → step through).
- Queued prompts can be edited: the pencil, a double-click, or ↑ in an empty composer.
- Only one Nomad.ai runs: launching it again brings the open window forward.
- Middle-click autoscroll is 1.5× faster.

## v1.3.0

- Switching the model of a conversation with history asks first (its cache does not carry over); also for a typed
  `/model <name>`. Settings › Claude Code turns the question off.
- Slash command echoes (`<local-command-stdout>` and the like) no longer show as your prompts: their output is a muted line.
- Inline code is real text: a selection can start, end and copy inside it.
- The working row says what is happening ("Ping the loopback…", "Reading MainViewModel.cs"), in semibold.
- Ctrl+1…9 opens the sidebar's sessions top to bottom; hold Ctrl to see their numbers. Models moved to Ctrl+Shift+1…4.
- Middle-click autoscroll, as in a browser.
- Close to the tray (Settings › General › Window): closing hides Nomad.ai to the notification area.

## v1.2.0

- Prompt suggestions: after a turn, Claude Code's guess at your next prompt shows as grey text in the empty composer;
  Tab, → or a right-click puts it in, Esc dismisses it (Settings › Claude Code to turn it off).
- The CLI now runs with the SDK entrypoint (`sdk-cli`), which its prompt suggestions need.
- Dev flags: `--dev-log` writes the lines to and from the CLI, `--dev-cli-debug` the CLI's debug log.

## v1.1.1

- Sidebar: the Idle heading folds its list too (remembered between runs).

## v1.1.0

- Composer: attach any file or folder (+ menu, drop or paste); images stay images, other files go in as @-mentions and
  show as chips, also in the sent prompt. Image previews are larger and their remove button is no longer cut off.
- The working row shows the desktop app's own working mark, a different animation for thinking, reading, searching,
  running commands, writing and waiting for you (read from the installed Claude app; a pixel spinner without it).
- Sidebar: a row shows the working mark while Claude works, a yellow dot while it asks something, and a blue dot when
  it finished and you haven't looked yet. Group headings and the Alive heading fold their lists; Alive and Idle have
  count badges.
- Sessions untouched for 24 hours are archived on their own (Settings › General › Sessions; at least 1 hour, or off).
- Thinking between tool calls folds into the activity group as in the desktop app: only text ends a group.
- Background-task notifications show as a muted line instead of a prompt bubble.

## v1.0.0

- The executable is now `Nomad.ai.exe`: Discord's game list has a `nomad.exe`, so Discord put its overlay on Nomad.
- Sidebar: archive and delete sessions from their context menu (deleting moves the transcript to the Recycle Bin),
  list options behind a sliders icon (group by date, folder or not at all; show archived), grouped by date by
  default, and a width you drag at its right edge.
- Sessions another Nomad window or a terminal is running show up as Alive there too, and can't be resumed twice.
- Composer: paste, drop or attach images; a `/` menu of Claude Code's commands; prompts written while Claude works
  are queued and sent when it is done, or at once with Send now; its buttons stay level with the first line.
- Each model keeps the effort last chosen with it; models start at High, except Opus 5.5 (the Default effort).
- "Show activity" (in the + menu) hides the thinking and tool lines and keeps the replies.
- Motion: new transcript items rise in, thinking and tool lines fold open, and the working row has a moving shimmer.
- Text: a selection can start beside the text, as in a browser; code is a size larger; the caret is white.
- Anthropic Sans and Mono are found in the Microsoft Store version of the Claude app too.

## v0.1.0

- First release of Nomad.ai, a native Windows client for Claude Code. It starts your installed `claude` CLI and
  drives it over stream-json, so sign-in, settings, MCP servers and sessions are the CLI's own.
- Sessions from `~/.claude/projects` in the sidebar, split into Alive and Idle, with search, history, resume,
  generated titles, rewind and fork.
- Streaming markdown, collapsible tool groups with diff counts, the thought process, permission and question cards.
- Remote Control: continue sessions from the Claude app or claude.ai, and start new ones from the phone.
- Status row with folder, permission mode, model chips, an effort slider and a context ring with plan usage limits.
- Settings for fonts, text sizes, transcript width, the CLI path and session defaults.
- Installed with Setup, Nomad.ai updates itself from GitHub releases.
