# Changelog

Released with tags `v<version>` by `.github/workflows/release.yml`, which needs a non-empty `## v<version>` section here.
Newest first: the newest heading is also the version of local builds (`Directory.Build.props`).

## v1.0.0

- First release: a native Windows client for Claude Code. Klodi starts your installed `claude` CLI and drives it over
  stream-json, so sign-in, settings, MCP servers and sessions are the CLI's own.
- Sessions from `~/.claude/projects` in the sidebar, with search, history, resume and generated titles.
- Streaming markdown, collapsible tool groups with diff counts, the thought process, permission cards (Enter allows,
  Esc denies) and question cards.
- Model, effort, fast mode, permission mode and folder from the status row, and a ring showing context use.
- Settings for fonts, text size, transcript width, hardware acceleration, the CLI path and session defaults.
- Installed with Setup, Klodi updates itself from GitHub releases.
