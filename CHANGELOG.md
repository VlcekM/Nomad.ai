# Changelog

Released with tags `v<version>` by `.github/workflows/release.yml`, which needs a non-empty `## v<version>` section here.
Newest first: the newest heading is also the version of local builds (`Directory.Build.props`).

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
