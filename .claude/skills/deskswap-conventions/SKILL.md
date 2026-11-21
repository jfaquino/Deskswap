---
name: deskswap-conventions
description: 'Deskswap project conventions: desktop model, layer boundaries, Win32 interop via CsWin32, safe window hiding, hooks and threading, persistence, performance and testing. Use when designing, writing or reviewing any Deskswap code.'
---

# Deskswap Conventions

Deskswap is a Windows 11 tray app (.NET 10, WinForms) that gives multi-monitor setups a shared pool
of virtual desktops. Apply these rules to all code in this repo. Where a generic skill disagrees,
this skill wins.

## Product model

`docs/design.md` is the source of truth for behavior. Read the relevant section before implementing
or changing behavior, and update it in the same change when a decision changes. In short:

- **Shared pool:** desktops `1…N` are global. Each monitor shows exactly one; a desktop is shown on at
  most one monitor. Windows belong to a desktop and move with it between monitors.
- **Swap on conflict:** going to a desktop already shown on another monitor swaps the two monitors' desktops.
  Next/previous skip desktops shown elsewhere and stop at the ends.
- **macOS-style lifecycle:** desktops are added and removed only explicitly. Empty desktops are kept, and numbers never shift on their own.
- Shortcuts act on the monitor under the mouse cursor.
- Deskswap does **not** use or patch Windows' own virtual desktops. It hides/shows top-level windows
  and takes over the desktop shortcuts with a low-level keyboard hook.
- Never use undocumented virtual-desktop COM interfaces or inject into `explorer.exe`. The only
  native desktop API allowed is the documented `IVirtualDesktopManager`, to ignore windows that sit
  on another Windows desktop.

## Layers

| Project | May reference | Must not contain |
|---|---|---|
| `Deskswap.Core` (`net10.0`) | BCL only | Win32, WinForms, `System.Drawing`, file/registry I/O, timers, static mutable state |
| `Deskswap.Platform` (`net10.0-windows`) | Core | UI, desktop-model decisions |
| `Deskswap.Ui` (`net10.0-windows`, WPF) | Core, Platform | Desktop-model decisions; it shows an `OverviewState` and reports user intent through `IOverviewActions` |
| `Deskswap.App` (`net10.0-windows`, WinForms) | Core, Platform, Ui | Desktop/window logic that belongs in Core |

- Core owns the model (desktops, monitors, window assignments, pins, rules, layout snapshots) and
  declares the interfaces it needs (window system, clock, storage). Platform implements them; App
  wires everything together by hand in one composition root. No DI container.
- Core operations are deterministic: they take inputs and return the set of changes (show/hide/move)
  for Platform to apply. That keeps every behavior unit-testable without real windows.
- Use domain names: `Desktop`, `Monitor`, `ManagedWindow`, `Swap`, `Pin`, `Rule`, `Layout`.

## Win32 interop

- All P/Invoke goes through **CsWin32**: add the API name to `src/Deskswap.Platform/NativeMethods.txt`
  and call `PInvoke.X`. No hand-written `[DllImport]`/`[LibraryImport]` unless CsWin32 can't express it.
- Keep `HWND`/`HMONITOR` and other generated types inside Platform. Expose `nint` or small
  value types (`WindowId`, `MonitorId`) across the boundary.
- Check every return value that can fail. Read `Marshal.GetLastPInvokeError()` when the API sets it.
- Window handles get reused. Identify a live window by `(hwnd, process id, process start time)`.
  For persistence and rules, match by executable path + window class + optional title pattern, never by HWND.
- Identify monitors by their stable device path/EDID-based ID (Display Configuration APIs), not
  `Screen.DeviceName` (`\\.\DISPLAYn` changes after docking or reboot).

## Hiding windows safely

A window left hidden after a crash is the worst bug this app can have.

- **Write-ahead journal:** record a window in the recovery journal (flushed to disk) *before* hiding it.
  Remove it after it is shown again.
- A separate watchdog process restores everything in the journal if the main process dies.
  On startup, restore any windows left in an orphaned journal.
- Restore all windows on normal exit, on "Show all windows" in the tray menu, and when a monitor disconnects.
- Only manage eligible windows: visible, top-level, unowned, not cloaked, has a title, not a tool
  window (unless `WS_EX_APPWINDOW`), not ours, not shell classes (`Progman`, `WorkerW`, `Shell_TrayWnd`, `Shell_SecondaryTrayWnd`, …).
- Windows of elevated processes can't be hidden from a non-elevated app: detect this and treat them
  as unmanaged (always visible). Never fail silently.

## Hooks and threading

- All model state lives on the **UI (STA) thread**. Core types are not thread-safe unless documented.
- Hook callbacks (`WH_KEYBOARD_LL`, `SetWinEventHook`) must return almost immediately. Do no blocking
  work, no I/O and no `SendMessage` to other processes inside them, and let no exceptions escape.
  Enqueue the event and process it on the UI thread with `BeginInvoke`. Windows silently removes
  low-level hooks that exceed `LowLevelHooksTimeout`.
- Keep hook delegates rooted in a field for the hook's lifetime, and unhook in `Dispose`.
- `async`: use `ConfigureAwait(false)` in Core/Platform only, never in App.

## Performance

- Event-driven first: WinEvent hooks (`EVENT_OBJECT_SHOW`, `EVENT_OBJECT_HIDE`, `EVENT_OBJECT_DESTROY`,
  `EVENT_SYSTEM_FOREGROUND`, `EVENT_OBJECT_LOCATIONCHANGE`, …) drive updates. A slow reconciliation
  sweep (seconds, not milliseconds) is only a safety net.
- Don't write to disk when nothing changed. Take window pictures only when the overview opens, when a desktop is hidden, and (unless the user chose app icons for minimized windows) of the window in use every few seconds when something changed; card-sized, in memory only, never on disk. Never while Deskswap's own windows are on screen.
- Avoid per-window process lookups in hot paths. Cache process info by `(pid, start time)`.

## Persistence

- Data lives under `%LOCALAPPDATA%\Deskswap\` (`ProductInfo.DataFolderName`).
- Use `System.Text.Json` with source generation and versioned schemas (`"schemaVersion": n`).
- Write atomically: write a temp file, then replace. If a file is corrupt, back it up as
  `*.corrupt-<timestamp>` and fall back to defaults. Never silently overwrite user data.
- Settings changes merge field by field. Adding a new shortcut command must not reset existing bindings.

## Error handling

- No empty `catch {}`. Catch specific exceptions, log them, and decide: retry, skip the window, or
  fail safe (restore all windows).
- Throw `ArgumentException`/`ArgumentNullException` (`ArgumentNullException.ThrowIfNull`) for
  programmer errors in public Core APIs.

## Style

- Builds use nullable, warnings-as-errors and `latest-recommended` analyzers (`Directory.Build.props`). Fix warnings, don't suppress them. If a suppression is unavoidable, scope it narrowly with a justification.
- Add package versions only in `Directory.Packages.props`.
- XML docs on public types and members (see the `csharp-docs` skill). Code and comments are in English.
- Prefer `sealed` classes, `readonly` fields, records for value-like data, file-scoped namespaces.

## Testing

- xUnit v3 on Microsoft Testing Platform (see the `csharp-xunit` skill). Only the `xunit.v3` package.
- Core: thorough unit tests for every model operation, especially swap, pin, rules, monitor
  connect/disconnect, and layout save/restore round-trips.
- Platform/App: keep logic thin. Verify with a manual smoke checklist on a multi-monitor machine.

## Commands

```powershell
dotnet build
dotnet test
dotnet run --project src/Deskswap.App
```
