# Nomad.ai — design reference

Nomad.ai is a native Windows client for Claude Code sessions. Its look is the Claude desktop app's
**Code** tab as it was before the late-October-2026 restyle: warm near-black surfaces, terracotta
accent, light font weights, dense rows, normal-sized text. This file is the written reference for
that look; the original screenshots live only in the conversation that started the project, so when
in doubt prefer: smaller, lighter, quieter, warmer.

The values below are measured off 1920×1080 screenshots at 100 % scaling. Colours marked ≈ are
estimates to be tuned by visual review; the accent values are exact brand tokens.

## 1. Palette (dark only for v1)

| Token              | Value                        | Used for                                              |
|--------------------|------------------------------|-------------------------------------------------------|
| `Bg.Sidebar`       | ≈ `#191918`                  | sidebar                                               |
| `Bg.Main`          | ≈ `#1E1E1D`                  | window / transcript background, popups' base          |
| `Bg.Raised`        | ≈ `#262625`                  | user bubble, search box, selected sidebar row, cards  |
| `Bg.Hover`         | `rgba(255,255,255,0.04)`     | row hover                                             |
| `Bg.Input`         | ≈ `#202020`                  | composer                                              |
| `Bg.Code`          | ≈ `#161615`                  | fenced code blocks                                    |
| `Border`           | ≈ `#333331`                  | composer, cards, popups, sidebar divider              |
| `Border.Subtle`    | `rgba(255,255,255,0.06)`     | separators inside lists                               |
| `Text.Primary`     | ≈ `#E9E7E2`                  | body text, titles                                     |
| `Text.Secondary`   | ≈ `#A8A6A0`                  | tool-group lines, "· Max", meta                       |
| `Text.Muted`       | ≈ `#7B7975`                  | section headers ("Recents"), placeholders, shortcuts  |
| `Accent`           | `#D2784B` = hsl(20 60% 56%) | AGENT label, running indicator, primary button, caret |
| `Accent.Emphasis`  | `#BE6437` = hsl(20 55% 48%) | primary button hover / pressed                 |
| `Accent.Subtle`    | `rgba(210,120,75,0.14)`      | accent-tinted backgrounds (card headers)              |
| `Success`          | ≈ `#5CB97A`                  | "Done", `+21` diff badge text                         |
| `Danger`           | ≈ `#E0605A`                  | errors, `−1` diff badge text, Deny hover              |
| `Link`             | ≈ `#7AA7F5`                  | markdown links (the old UI used blue links)           |
| `Unread`           | ≈ `#4F8FF7`                  | small dot on a sidebar row with unseen output         |

Window close-button hover: `#C42B1C` (Windows convention). Scrollbars: 6 px, thumb
`rgba(255,255,255,0.12)`, no track, no arrows, appear on hover.

## 2. Typography

- **UI font:** Inter (bundle `Avalonia.Fonts.Inter`), fallback Segoe UI. The original used
  Anthropic Sans, which is proprietary, so the font family must be a setting the user can change.
- **Weights:** 400 everywhere in the chrome. 500 only for the selected sidebar row, button labels
  and card titles. 600 only for markdown `**bold**` and transcript headings. Never 700.
- **UI text:** 14 px. Section headers ("Recents", "Model · this session"): 12 px, `Text.Muted`.
  Meta lines ("Ran 2 commands ›", status row): 13–14 px `Text.Secondary`.
- **Uppercase labels** ("AGENT", "AGENT CREW", "GENERAL"): 11 px, weight 500, letter-spacing 1 px,
  `Accent`.
- **Transcript body:** 15 px, line-height 1.5, `Text.Primary`; paragraphs 12 px apart.
  h1 20 px / h2 17 px / h3 15 px, weight 600, 18 px above, 8 px below. Lists indent 24 px, bullet
  "•" in `Text.Secondary`, 4 px between items. Blockquote: 2 px left border `Border`, text
  `Text.Secondary`.
- **Monospace:** Cascadia Mono (ships with Windows 11), fallback Consolas, 13 px. Inline code:
  `Bg.Raised` background, 1 px `Border`, radius 4, padding 1 px 5 px, `Text.Primary`. Fenced code:
  `Bg.Code`, 1 px `Border`, radius 8, padding 12 px, horizontal scroll, language label 11 px muted
  top-right, copy button on hover.
- **Text size setting** (Small / Medium / Large) scales transcript body 14 / 15 / 16 px and code
  12 / 13 / 14 px. The chrome never scales.

## 3. Window layout (default 1400×900, minimum 960×600)

```
┌──────────────────────────────────────────────────────────────────────┐
│ ≡ ◫  ‹ ›        Session title ˅                           ─  □  ✕  │  title bar 36 px
├────────────┬─────────────────────────────────────────────────────────┤
│ Search     │                                                         │
│ + New      │     transcript column (centred, max width by setting)   │
│            │                                                         │
│ Recents  + │                                                         │
│ row        │                                                         │
│ row (sel.) │  AGENT Done · Opus 5.5 · last turn 1:08 · 2.9k tokens   │  status line
│ row        │  ┌───────────────────────────────────────────────┐      │
│            │  │ +  Type / for commands                      ↵ │      │  composer
│            │  └───────────────────────────────────────────────┘      │
│ MV Martin ⚙│  📁 Nomad   Accept edits                Opus 5.5 ˅  ◯   │  status row 32 px
└────────────┴─────────────────────────────────────────────────────────┘
   320 px
```

- **Title bar:** frameless window, client area extended into the title bar, own caption buttons
  (46×36 each, hover `Bg.Hover`, close hover `#C42B1C`). Left group: sidebar toggle, back/forward
  (may be inert placeholders). Session title 14 px 400 with a "˅" chevron; the empty state says
  "New session". The whole bar drags the window; double-click maximises.
- **Sidebar:** 320 px, `Bg.Sidebar`, 1 px right `Border`. Collapsible (toggle in the title bar).
  - Search box: margin 10 px 12 px, height 30, radius 8, `Bg.Raised`, magnifier icon 14 px,
    placeholder "Search". Filters the recents list as you type.
  - Nav rows: "＋ New" (Ctrl+N). Row height 28, padding 0 10 px, radius 8, icon 16 px
    `Text.Secondary`, label 14 px. The row for the current empty session is `Bg.Raised`.
  - "Recents" header: 12 px muted, 20 px above, with a "+" icon button at the right edge.
  - Recent rows: height 30, one line, ellipsis, 14 px 400. Hover `Bg.Hover`, selected
    `Bg.Raised` + weight 500. Right edge: a 6 px `Accent` dot while that session's turn is running,
    a 6 px `Unread` dot when it finished while not selected. Tooltip shows the folder path and
    time. Sorted by last activity; the list is built from `~/.claude/projects` (see README).
  - Bottom bar: 48 px, 1 px top `Border.Subtle`. 28 px circle avatar with initials
    (`Bg.Raised`, 12 px 500), name 14 px, "· Max"-style secondary text if known, chevron;
    gear icon at the right opens Settings.
- **Transcript column:** centred, max width Narrow 720 / Medium 920 / Wide 1160 (setting; default
  Wide), 24 px side padding, 24 px top padding. Scrolls as a whole; auto-follows the bottom while
  streaming unless the user scrolled up (then a small "↓" round button appears bottom-centre, as in
  the original).
- **Composer:** in the same column, 16 px above the status row. Radius 12, 1 px `Border`,
  `Bg.Input`, min height 44, grows to ~8 lines, padding 10 px 12 px. Left: "+" icon button
  (menu: Open folder…). Placeholder "Type / for commands" inside a session, "Describe a task or
  ask a question" on a new session. Enter sends, Shift+Enter inserts a newline. Right: a send
  button (↵ glyph, `Accent`) that only appears when there is text; while a turn runs it becomes a
  stop button (■).
- **Status row:** 32 px under the composer, 14 px text. Left, each a flat clickable chip with an
  icon: folder name (opens the folder menu), permission mode (opens the mode menu). Right: model
  name in `Text.Primary` + effort in `Text.Secondary` (opens the model menu), then a 14 px ring:
  outlined `Text.Muted` when idle, spinning `Accent` arc while a turn runs.

## 4. Transcript blocks

- **User message:** right-aligned bubble, max 75 % of the column, `Bg.Raised`, radius 12,
  padding 10 px 14 px, 15 px text, 20 px vertical margin. Plain text (preserve newlines).
- **Assistant text:** no bubble, full column width, rendered markdown (Markdig AST → Avalonia).
  Streams in place: re-render only the last block on each delta. Links open in the browser.
- **Thinking:** one collapsed muted line "Thinking…" (animated dots while streaming) that expands
  to the thinking text in `Text.Secondary` 14 px. Never shown by default.
- **Tool group line:** consecutive tool calls between two text blocks collapse into one 14 px
  `Text.Secondary` line: "Ran 2 commands ›", "Edited 2 files, ran 2 commands  +21 −1 ›",
  "Used 6 tools ›", "Read 3 files ›". While running: "Running a command…" with a 12 px spinner.
  `+21` / `−1` are 12 px pills (`Success` / `Danger` text on a 12 % tint of the same colour).
  Click expands a list: one row per tool — 16 px icon, tool name 13 px 500, one-line summary
  (Bash: the command in mono; Read/Edit/Write: the path; Grep/Glob: the pattern; others: first
  input value) and a status glyph (✓ / ✕ / spinner). Each row expands further to the raw
  input and result in a mono 13 px box (max 20 lines, scrolls).
- **Permission card:** `Bg.Raised`, 1 px `Border`, radius 10, padding 14, 8 px above/below.
  Title 14 px 500 ("Claude wants to run a command" / "…edit a file" / "…use <tool>"), then the
  tool summary (mono for commands; for Edit show old → new as a mini diff with `Success` /
  `Danger` tinted lines), then buttons right-aligned: **Allow** (primary: `Accent` background,
  `#1A1A1A` text, hover `Accent.Emphasis`), **Allow for session** (secondary: `Bg.Raised` +
  1 px `Border`), **Deny** (ghost, `Text.Secondary`, hover `Danger`). Enter = Allow, Esc = Deny.
  When answered, the card collapses to a one-line muted record ("Allowed Bash" / "Denied Edit").
- **Question card** (AskUserQuestion, if implemented): same card style, question 14 px 500,
  options as rows with radio behaviour, "(Recommended)" kept in the label, free-text row.
- **Result / error:** a turn that ends with an error shows a card with `Danger` left border and
  the message in 14 px.
- **Agent status line:** shown above the composer once a session has run: "AGENT" (uppercase
  accent label) + state — "Running" in `Accent` with animated dots, "Done" in `Success`, "Error"
  in `Danger`, "Interrupted" in `Text.Secondary` — then `Text.Secondary` 13 px items separated
  by " · ": model, "last turn 1:08", "2.9k tokens", "13 tools". Right edge: a small square
  button (■, 24×24, `Bg.Raised`) that interrupts while running.

## 5. Menus and popups

Flyouts anchored to the chip that opened them: `Bg.Main`, 1 px `Border`, radius 10, shadow
`0 8px 24px rgba(0,0,0,0.45)`, padding 6, min width 220. Items: height 32, radius 6, 14 px,
hover `Bg.Hover`; selected item shows "✓" at the right in `Text.Primary`; numbered shortcut hints
("1".."4") 13 px `Text.Muted` at the right; two-line items put a 12 px `Text.Secondary`
description under the label. Section headers 12 px `Text.Muted`. A 1 px `Border.Subtle` rule
between sections.

- **Model menu:** header "Model · this session"; Opus 5.5, Fable 5.1, Sonnet 5.5, Haiku 5.5
  (shortcuts 1–4, Ctrl+Shift+1..4 while the composer is focused); rule; "Effort  Medium ›" (sub-menu
  Low / Medium / High); rule; "Fast mode" header with an "Enable fast mode" toggle.
- **Mode menu:** header "Mode"; Manual — "Always ask before making changes"; Accept edits —
  "Automatically accept all file edits"; Plan — "Create a plan before making changes";
  Bypass permissions — "Skip all permission prompts" (shown in `Danger` when selected).
- **Folder menu:** search box "Search folders…", "No folder", "Recent" header with the recent
  folders (from `~/.claude/projects`), rule, "Open folder…".

## 6. New-session view

Transcript column empty except the six latest projects as cards (280×142, `Bg.Sidebar`, 1 px
`Border.Card`, radius 10, 10 px apart, wrapping): folder name 500 + branch pill (mono 11 px on
`Bg.Chip`), path 11 px muted, up to three commits (subject 12 px `Text.Chrome`, age 11 px muted), and a
footer ("3 changed" in `Waiting` · "Last session 2 hours ago"). The git part fades in once read. Hover
lightens the border; the session's folder has a `Check` border and ✓. Above them, "Recent projects" and
the "No folder" / "Open folder…" links. Clicking a card sets the session folder. Composer placeholder "Describe a task or ask a
question". Status row shows the folder chip ("No folder" when none), mode, model.
Optional flourish: a small 16×12 pixel-art critter in `Accent` sitting at the right end of the
composer's top edge, as the original had.

## 7. Settings

A modal 760×520 over the window: left nav (General, Claude Code) 14 px rows in a 180 px column;
right pane with a 16 px 600 section title and rows (label 14 px + 13 px muted description on the
left, control on the right, 1 px `Border.Subtle` between rows):

- General › Appearance: Interface font (combo of installed families, default Inter), Code font
  (default Cascadia Mono), Transcript text size (segmented Small / Medium / Large, selected
  segment `Bg.Raised`), Transcript width (Narrow / Medium / Wide).
- Claude Code: CLI path (default `claude` on PATH), default model, default permission mode,
  default effort.

Persisted as JSON in `%APPDATA%\Nomad\settings.json`.

## 8. Out of scope for v1

Light theme, chat (non-Code) mode, Artifacts, Scheduled, Customize, Agent Crew, worktrees, remote control, voice, file pane, slash-command
palette (slash commands are just sent as text), MCP configuration UI.
