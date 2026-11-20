# Deskswap

**Swap desktops between your monitors.**

Deskswap brings macOS/tiling-WM-style multi-monitor desktops to Windows 11. Windows' built-in
virtual desktops switch every monitor at once; Deskswap keeps one shared pool of desktops and lets
each monitor show any one of them.

## How it works

- **Shared pool (default):** desktops `1…N` are global. Each monitor shows one; switching on one
  monitor leaves the others alone. Windows belong to a desktop and travel with it.
- **Swap on conflict:** picking a desktop that another monitor is already showing swaps the two.
- **Monitor-owned mode (option):** each monitor keeps its own desktops (like macOS
  "Displays have separate Spaces").

Deskswap does not patch Windows. It hides and shows top-level windows per monitor and takes over the
virtual-desktop shortcuts (e.g. `Win+Ctrl+←/→`) with a low-level keyboard hook.

## Planned features

1. Shared desktop pool with swapping, per-monitor switching shortcuts, crash-safe hiding
   (write-ahead journal + watchdog process).
2. New windows join the current desktop instantly; pin apps/windows to all desktops.
3. App rules (app → desktop) and layout restored after reboot.
4. Overview/switcher, settings UI with editable shortcuts, start with Windows.
5. Polish: animations, desktop names, touchpad gestures.

## Project layout

| Project | Target | Purpose |
|---|---|---|
| `src/Deskswap.Core` | `net10.0` | Pure domain logic (desktops, monitors, assignments, rules, layout). No Win32. |
| `src/Deskswap.Platform` | `net10.0-windows` | Win32 integration via [CsWin32](https://github.com/microsoft/CsWin32) (`NativeMethods.txt`). |
| `src/Deskswap.App` | `net10.0-windows` | WinForms tray app, overview, settings. Builds `Deskswap.exe`. |
| `tests/Deskswap.Core.Tests` | `net10.0` | xUnit v3 tests for Core. |

Shared build settings live in `Directory.Build.props`; package versions in `Directory.Packages.props`
(central package management).

## Build and run

Requires Windows 11 and the .NET 10 SDK (pinned in `global.json`).

```powershell
dotnet build
dotnet test
dotnet run --project src/Deskswap.App
```
