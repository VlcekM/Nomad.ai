# Changelog

Released with tags `v<version>` by `.github/workflows/release.yml`, which needs a non-empty `## v<version>` section here.
Newest first: the newest heading is also the version of local builds (`Directory.Build.props`).

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
