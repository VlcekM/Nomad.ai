# Notes for coding agents

## Motion

Animate every UI change nicely and smoothly, but unobtrusively: things that appear, disappear,
grow or move ease in rather than pop, and never call attention to themselves.

- 120–250 ms, `CubicEaseOut` (or the same curve by hand); fades and small slides, no bounces.
- Never make input wait on an animation; a new value interrupts and retargets the running one.
- Timers that drive animation run only while something is moving or visible, and stop when idle.
- New streamed text fades in; rows that come and go fold their height and fade.
- Check animations mid-flight with `--dev-key` (see below), not only their end states.

## Seeing the app

Never start the app visibly to check a change: the user works on this PC. Take a screenshot instead.
`--dev-screenshot` keeps the window invisible (DWM-cloaked before it is first shown, unfocused, no
taskbar button), renders it to a PNG and exits, so it never interrupts the user.

Build into a scratch folder, not `bin\`, so the user's own running instance (which locks
`bin\Debug\...\Nomad.ai.exe`) is never in the way and never has to be stopped:

```powershell
dotnet build Nomad.App -o <scratch>\build
<scratch>\build\Nomad.ai.exe --dev-size 1400x900 --dev-after 5 --dev-screenshot <scratch>\shot.png
```

Then look at the PNG. Useful extras (all listed in `Nomad.App/Infrastructure/DevOptions.cs`):

- `--dev-menu model|mode|folder|branch|plus|usage|remote` opens a status-row menu first (`list` / `alivelist` the Idle / Alive list options, `row` an Idle row's context menu, `group` a folder heading's, `composer` the composer's edit menu); `--dev-settings-open` the settings;
  `--dev-side-panel` the right-hand panel; `--dev-files [a|b|…]` a chat's Files view, with those files open in tabs (`--dev-files-split` opens the last to the side too).
- `--dev-compose "/co"` types into the composer, `--dev-paste-image <file>` attaches an image or file, and
  `--dev-drag x1,y1,x2,y2 <seconds>` drags the mouse inside the window (clicks, text selection; add
  `--dev-drag-ctrl` for a Ctrl+click).
- `--dev-key Ctrl+B <seconds>` presses a key on the focused control. Every capture waits 600 ms
  after `--dev-after`, so to catch an animation press the key about that long after it.
- `--dev-prompt "..." --dev-model claude-haiku-5-5 --dev-lean --dev-wait-turn --dev-after 60` runs a
  real turn on the cheapest model before the shot.
- `--dev-settings <copy of %APPDATA%\Nomad\settings.json>` tries settings without touching the user's.
- `--dev-cwd <scratch folder>` keeps test sessions out of real projects.

Screenshot runs don't save the window size or placement.

Start the app visibly only when the user asks for it (`dotnet run --project Nomad.App`).
