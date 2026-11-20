# Deskswap

**Swap desktops between your monitors.**

[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)
![Platform: Windows 11](https://img.shields.io/badge/platform-Windows%2011-0078D4)
![.NET 10](https://img.shields.io/badge/.NET-10-512BD4)

Windows 11's virtual desktops switch **every monitor at once**. Deskswap gives multi-monitor setups the
macOS experience instead: one shared pool of desktops, each monitor shows one of them, and switching a
desktop on one screen leaves the others alone.

> **Status: early development.** Milestones 1 and 2 are done. See the [roadmap](#roadmap).
> Expect rough edges, and keep the emergency shortcut (`Win+Ctrl+Shift+F12`) in mind.

## How it works

- **One shared pool of desktops.** Desktops `1…N` are global. Each monitor shows exactly one, and a
  desktop is never shown on two monitors at once.
- **Shortcuts act on the monitor under your mouse.** No need to click into a screen first.
- **Swap on conflict.** Go to a desktop that another monitor is showing, and the two monitors trade
  desktops. Windows travel with their desktop, keeping their relative position, size and
  maximized or minimized state.
- **macOS-style desktops.** You add and remove desktops yourself; empty desktops are kept and numbers
  never shift on their own.
- **Your stack is kept.** When a desktop comes back, its windows return in the order they were stacked,
  and the top one gets focus.
- **New windows land where you open them, instantly**, and a window that asks for attention (you click its
  notification, or relaunch the app) switches its monitor to its desktop, like macOS.
- **Monitors come and go.** Unplug a monitor and its desktop stays in the pool with its windows hidden;
  plug it back in and it gets that desktop again.

Deskswap doesn't patch Windows or use undocumented APIs. It hides and shows top-level windows per
monitor and takes over the virtual-desktop shortcuts with a low-level keyboard hook.

## Shortcuts

| Shortcut | Action |
|---|---|
| `Win+Ctrl+←` / `Win+Ctrl+→` | Previous / next desktop on the monitor under the mouse (skips desktops shown elsewhere; stops at the ends) |
| `Win+Ctrl+1…9` | Go to desktop N (swaps if another monitor shows it) |
| `Win+Ctrl+Shift+←` / `Win+Ctrl+Shift+→` | Move the focused window to the previous / next desktop; the monitor follows it |
| `Win+Ctrl+Shift+1…9` | Move the focused window to desktop N (to the other monitor if that one shows N) |
| `Win+Ctrl+Shift+P` | Pin the focused window to every desktop of its monitor, or unpin it |
| `Win+Ctrl+D` | Add a desktop and switch to it |
| `Win+Ctrl+F4` | Remove the current desktop (its windows move to the desktop that takes its place) |
| `Win+Ctrl+Shift+F12` | **Emergency:** show all windows and pause; press again to resume |

A short label ("Desktop 3") appears on the monitor that switched.

`Win+Ctrl+Shift+1…9` replaces Windows' own "open taskbar app N as administrator" shortcut while Deskswap
runs.

**Touchpad:** in *Settings → Bluetooth & devices → Touchpad → Advanced gestures*, set the four-finger
swipe left/right to custom shortcuts `Win+Ctrl+←` / `Win+Ctrl+→` to switch only the monitor under the
cursor.

## Safety

Hiding windows is the riskiest thing Deskswap does, so losing one is designed out:

- **Write-ahead journal:** every window is recorded on disk *before* it is hidden.
- **Watchdog:** a small helper process shows every journaled window if Deskswap crashes or is killed.
- **Startup recovery:** anything left hidden by a previous run is shown again on start.
- **Fail safe:** on any unexpected error Deskswap shows all windows and pauses.
- **Always reachable:** *Show all windows and pause* in the tray menu, or `Win+Ctrl+Shift+F12`.
  *Exit* shows every window too.

## Requirements

- Windows 11
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) to build (pinned in `global.json`)

## Build and run

```powershell
git clone https://github.com/jfaquino/Deskswap.git
cd Deskswap
dotnet build
dotnet test
dotnet run --project src/Deskswap.App
```

Deskswap runs in the system tray. Right-click the tray icon for *Show all windows and pause*,
*Resume* and *Exit*.

**Before you try it:** close other tools that use `Win+Ctrl+arrow` shortcuts or manage virtual
desktops, so they don't fight over the same keys. Keep a single Windows desktop; Deskswap manages
windows on the current one.

The UI is in English and Spanish and follows your Windows display language.

## Data and privacy

Deskswap works offline. It has no account, no telemetry and makes no network requests. Everything it
stores stays in `%LOCALAPPDATA%\Deskswap\`:

| File | Contents |
|---|---|
| `layout.json` | Desktops, which desktop each monitor shows, and window assignments |
| `journal.json` | Windows currently hidden (exists only while windows are hidden) |
| `deskswap.log` | A small diagnostic log, rotated at 1 MB |

## Known limitations

- Hidden windows disappear from the taskbar and `Alt+Tab`. That is how per-monitor desktops look.
  For the best taskbar experience, set *Settings → Personalization → Taskbar → Taskbar behaviors →
  Show my taskbar apps on → Taskbar where window is open*.
- Windows of apps running as administrator can't be hidden by a non-elevated app, so they stay
  visible on every desktop (Deskswap tells you once per session).

## Roadmap

| Milestone | Scope | Status |
|---|---|---|
| **M1** | Shared desktop pool with swap, per-monitor shortcuts, crash-safe hiding, indicator, tray menu, English and Spanish | ✅ Done |
| **M2** | Instant placement of new windows, switch to a window's desktop when it asks for attention, move-window shortcuts, pin windows to all desktops, monitor connect/disconnect | ✅ Done |
| **M3** | App rules, layout restored after reboot, settings window with shortcut editor, start with Windows, desktop names | Next |
| **M4** | `Win+Tab` overview with previews, drag and drop, add/remove/reorder desktops, slide animation | Planned |
| **M5** | Per-desktop wallpaper, installer, signed releases | Planned |

The full behavior specification is in [docs/design.md](docs/design.md).

## Project layout

| Project | Target | Purpose |
|---|---|---|
| `src/Deskswap.Core` | `net10.0` | Pure domain logic: desktop pool, shortcuts, geometry. No Win32. |
| `src/Deskswap.Platform` | `net10.0-windows` | Win32 integration via [CsWin32](https://github.com/microsoft/CsWin32): monitors, windows, journal, keyboard hook. |
| `src/Deskswap.App` | `net10.0-windows` | WinForms tray app. Builds `Deskswap.exe`. |
| `tests/Deskswap.Core.Tests` | `net10.0` | Unit tests for the domain model (xUnit v3). |
| `tests/Deskswap.Platform.Tests` | `net10.0-windows` | Journal and monitor tests. |

Shared build settings are in `Directory.Build.props` (nullable, warnings as errors, analyzers) and
package versions in `Directory.Packages.props`.

## Contributing

Issues and pull requests are welcome. Please read [docs/design.md](docs/design.md) first: it is the
source of truth for behavior, and changes to behavior should update it in the same pull request.
Run `dotnet build` and `dotnet test` before submitting; the build treats warnings as errors.

## Acknowledgements

Inspired by macOS Spaces ("Displays have separate Spaces") and by earlier Windows projects exploring
per-monitor desktops, including [IndepenDesk](https://github.com/harungecit/IndepenDesk) and
[DisplaySpaces](https://github.com/ctenhank/DisplaySpaces). Deskswap is an independent implementation.

## License

[MIT](LICENSE) © 2026 jfaquino
