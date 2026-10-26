# Notes for coding agents

## Seeing the app

Never start the app visibly to check a change: the user works on this PC. Take a screenshot instead.
`--dev-screenshot` keeps the window invisible (DWM-cloaked before it is first shown, unfocused, no
taskbar button), renders it to a PNG and exits, so it never interrupts the user.

Build into a scratch folder, not `bin\`, so the user's own running instance (which locks
`bin\Debug\...\Nomad.exe`) is never in the way and never has to be stopped:

```powershell
dotnet build Nomad.App -o <scratch>\build
<scratch>\build\Nomad.exe --dev-size 1400x900 --dev-after 5 --dev-screenshot <scratch>\shot.png
```

Then look at the PNG. Useful extras (all listed in `Nomad.App/Infrastructure/DevOptions.cs`):

- `--dev-menu model|mode|folder|plus|usage` opens a status-row menu first; `--dev-settings-open` the settings.
- `--dev-key Ctrl+B <seconds>` presses a key on the focused control. Every capture waits 600 ms
  after `--dev-after`, so to catch an animation press the key about that long after it.
- `--dev-prompt "..." --dev-model claude-haiku-5-5 --dev-lean --dev-wait-turn --dev-after 60` runs a
  real turn on the cheapest model before the shot.
- `--dev-settings <copy of %APPDATA%\Nomad\settings.json>` tries settings without touching the user's.
- `--dev-cwd <scratch folder>` keeps test sessions out of real projects.

Screenshot runs don't save the window size or placement.

Start the app visibly only when the user asks for it (`dotnet run --project Nomad.App`).
