# Deskswap Design

Status: draft v1 (2026-11-20). This is the source of truth for behavior. Code and tests follow it.
When a decision changes, update this document in the same change.

## 1. Goal

Give Windows 11 multi-monitor users the macOS experience of virtual desktops: switching one screen
leaves the others alone. Deskswap keeps **one shared pool of desktops**, and each monitor shows one of them.

**Non-goals**
- Changing or patching Windows' own virtual desktops (they always switch every monitor at once).
- Window tiling or snapping.
- Windows 10 support. Windows 10 is out of support, so we target Windows 11 only.

## 2. Glossary

| Term | Meaning |
|---|---|
| **Pool** | The ordered list of Deskswap desktops. Position in the list = desktop **number** (1-based). |
| **Desktop** | A set of windows, with a stable internal id, an optional **name** and (later) a wallpaper. |
| **Monitor** | A connected display, identified by a stable hardware id, not `\\.\DISPLAYn`. |
| **Shown** | The desktop a monitor currently displays. Its windows are visible. |
| **Managed window** | A top-level window Deskswap assigns to exactly one desktop. |
| **Pinned window** | Visible on every desktop of the monitor it's on. |
| **Unmanaged window** | Ignored by Deskswap: not eligible (§7), or elevated (§6.4). |
| **Target monitor** | The monitor a shortcut acts on: **the monitor under the mouse cursor**. |

## 3. Desktop model

### 3.1 Invariants

1. Every connected monitor shows exactly one desktop.
2. A desktop is shown on at most one monitor.
3. Pool size ≥ number of connected monitors. Maximum 16 desktops. Direct shortcuts cover 1–9.
4. Every managed window belongs to exactly one desktop. Its visibility = "its desktop is shown".
5. Pinned and unmanaged windows are never hidden by Deskswap.
6. Desktop numbers change only when the user adds, removes or reorders desktops. Never automatically.

### 3.2 Lifecycle (macOS-style)

- **First run:** the pool has one desktop per connected monitor. Monitor *k* (left to right) shows desktop *k*.
- Desktops are created and removed **only explicitly**. Empty desktops are kept.
- **Add** (`Win+Ctrl+D`): append a desktop at the end and switch the target monitor to it.
  The overview "+" only appends; it doesn't switch.
- **Remove** (`Win+Ctrl+F4` removes the desktop shown on the target monitor; the overview "×" removes any desktop):
  - Not allowed if it would break invariant 3. Show an indicator explaining why.
  - If the desktop is shown on monitor M, M switches to the nearest desktop not shown elsewhere
    (previous first, then next), and the removed desktop's windows move there.
  - If it isn't shown, its windows move to the desktop shown on the target monitor.
  - Desktops after it shift down by one.
- **Reorder:** drag in the overview (later milestone). Names and windows stay with their desktop.

### 3.3 Switching

| Action | Behavior |
|---|---|
| **Go to N** (`Win+Ctrl+N`) | If the target monitor already shows N: show the indicator only. If another monitor shows N: **swap**, so the two monitors trade desktops. Otherwise the target monitor shows N. If N doesn't exist: show the indicator only. |
| **Next / previous** (`Win+Ctrl+→/←`) | Move the target monitor to the next/previous desktop number that **isn't shown on another monitor** (skip those). At the end, stop: no wrap, indicator only. |
| **Swap** | Each monitor shows the other's desktop. Windows travel with their desktop (§3.5). |

On every switch the incoming desktop's windows come back **in the stacking order they had** (Windows puts
every shown window on top, so they are shown bottom-most first), and its top-most window gets focus. That
is the window that was active when the desktop was left, so no separate "last active" tracking is needed.

### 3.4 Moving windows

| Action | Behavior |
|---|---|
| **Move focused window to next/previous** (`Win+Ctrl+Shift+→/←`) | Target = next/previous desktop not shown on another monitor (same skip rule). The window moves there and the monitor **follows** (switches to it). At the end: indicator only. |
| **Move focused window to N** (`Win+Ctrl+Shift+N`) | If N is shown on another monitor, the window moves to that monitor and gets focus. Otherwise the window moves to N and its monitor follows. |
| **User drags a window to another monitor** | It joins the desktop shown on that monitor. |
| **Overview drag and drop** | Move a window to any desktop. The view doesn't follow. |

### 3.5 Windows travel with their desktop

When a desktop is shown on a different monitor than last time (swap, reconnect), its windows move to
the new monitor:
- Position and size are scaled to the new monitor's work area, then clamped to fit.
- Maximized windows stay maximized on the new monitor. Minimized windows stay minimized.
- Each window's position relative to the desktop's last monitor is remembered. Moving it back restores
  its exact rectangle when the monitor is the same.

### 3.6 Pinning

- **Pin window**: `Win+Ctrl+Shift+P` toggles the focused window (later also from the overview). The window
  stays visible on every desktop **of its monitor**, and stays on that monitor. Pins are saved in the layout.
- **Pin app**: a rule (§5.2) that pins every window of an app.
- Unpinning a window assigns it to the desktop currently shown on its monitor.

## 4. Monitors

- **Identity:** use a stable id from the Windows Display Configuration APIs (device interface path and
  EDID manufacturer/product/serial). `\\.\DISPLAYn` names change after docking or reboot.
- **Order** for "monitor k": left to right by position, then top to bottom.
- **Disconnect:** Deskswap hides the windows of the desktop that monitor was showing. The desktop
  **stays in the pool** and Deskswap remembers which monitor showed it. Windows moves the windows of a
  disconnected monitor elsewhere, so their position is treated as unknown: when the desktop is shown again,
  its windows are moved from wherever they actually are (§3.5).
- **Wait for displays to settle, then hold:** Windows changes the display arrangement in several steps
  (a reconnected monitor first appears with a temporary arrangement) and moves windows on its own while it
  does, both on disconnect and, with "remember window locations", on reconnect. Before every pass Deskswap
  compares a cheap display fingerprint (monitor handles, bounds, work areas) with its layout. If they differ,
  or a window is on an unknown monitor, it **stops all window decisions and ignores shortcuts** until the
  fingerprint has been stable for 0.75 s; it never reconciles with an incomplete view (missing windows would
  look closed). Then it applies the change and, for **3 s**, holds the layout: a window found on another
  monitor than its desktop's is moved back instead of being treated as dragged by the user.
- **Reconnect:** the monitor shows the desktop it showed before, if no other monitor is showing it.
  Otherwise it shows the lowest-numbered desktop not shown anywhere. If there is none, a desktop is
  added (invariant 3).
- **Resolution, DPI or arrangement changes:** re-clamp the windows on affected desktops. No change of
  desktop assignment.
- **Always:** if Deskswap loses track of a monitor, every window ever hidden for it must still be
  recoverable (§8).

## 5. New windows, rules and restore

### 5.1 Default placement

A new eligible window joins **the desktop shown on the monitor where it appears**.

### 5.2 Rules (explicit, user-defined)

A rule matches windows by **app** (executable path) and optionally by **window class** or a
**title pattern**. Its action is one of:
- **Always on desktop N** (by desktop id, so it survives reordering).
- **All desktops** (pin).
- **Ignore** (unmanaged).

Rules apply to every new window, at any time, and take priority over everything else. If a ruled
window opens on a desktop that isn't shown and the window is in the foreground (you just launched it),
its desktop is shown (§5.4). Otherwise it waits there hidden.

### 5.3 Auto-remember (restore after a reboot)

- Deskswap saves a **layout snapshot**: pool (order, names), monitor → desktop mapping, and for each
  managed window its app identity (exe path, class, title), desktop, monitor and rectangle.
- **Deskswap restarted, windows still alive:** match by window handle + process id + process start time.
  The layout returns exactly.
- **After a reboot or sign-in:** the pool and monitor mapping are restored at once. For a **restore
  period** (2 minutes after Deskswap starts, configurable), each new window is matched **one-to-one**
  against unused snapshot entries: same app, then the best title match. A match puts the window back
  on its saved desktop. Windows without a match use rules, then default placement.
- **Outside the restore period, auto-remember is off.** A new Chrome window you open on desktop 1 stays
  on desktop 1, even though Chrome "lived" on desktop 3 before. Only explicit rules move new windows.

### 5.4 Activation (macOS "switch to a Space with open windows")

When a window on a hidden desktop asks for attention, its monitor **switches to that desktop** and the
window comes to the front. Examples: an app shows its own hidden window, you click its notification,
or you relaunch an app that brings its existing window forward.

Detected as: an eligible window of a hidden desktop is visible without Deskswap having shown it. Window
events (show, foreground, title change, …) trigger a check within about 40 ms. The monitor the window is on
switches to its desktop; the rest of that desktop is brought from wherever it is.

- **Deskswap's own hides don't count.** Hiding is asynchronous, so a window Deskswap hid less than 1.5 s ago
  is treated as hidden even if it still looks visible.
- **Several at once:** the foreground window wins; windows of other hidden desktops are hidden again.
- **Enforce, don't follow, when the layout is being re-applied:** at startup, on resume and after a display
  change, visible windows of hidden desktops are hidden instead.
- **No ping-pong:** a window that triggers two switches within 10 s (an app that keeps showing itself right
  after being hidden) is not followed for a minute; it is hidden instead, and the log says so.

## 6. Shortcuts, input and feedback

### 6.1 Default shortcuts

All of these can be changed in Settings. Deskswap takes over the Windows shortcuts it uses through a
low-level keyboard hook.

| Shortcut | Action |
|---|---|
| `Win+Ctrl+←` / `Win+Ctrl+→` | Previous / next desktop on the target monitor (§3.3) |
| `Win+Ctrl+1…9` | Go to desktop N (swap if shown elsewhere) |
| `Win+Ctrl+Shift+←` / `Win+Ctrl+Shift+→` | Move the focused window to the previous / next desktop, following it |
| `Win+Ctrl+Shift+1…9` | Move the focused window to desktop N (if another monitor shows N, the window moves to that monitor). Replaces Windows' "open taskbar app N as administrator". |
| `Win+Ctrl+Shift+P` | Pin the focused window to every desktop of its monitor, or unpin it |
| `Win+Ctrl+D` | Add a desktop and switch the target monitor to it |
| `Win+Ctrl+F4` | Remove the desktop shown on the target monitor |
| `Win+Tab` | Toggle the Deskswap overview on every monitor |
| `Win+Ctrl+Shift+F12` | **Emergency:** show all windows and pause Deskswap; press again to resume |

- **Touchpad:** Windows gestures don't send keys by default. The help screen explains how to set the
  4-finger swipes to custom shortcuts in Settings → Bluetooth & devices → Touchpad → Advanced gestures:
  left/right → `Win+Ctrl+←/→`, up → `Win+Tab`.
- **Mouse side buttons** that send `Win+Ctrl+←/→` work automatically.
- The shortcut editor rejects duplicate bindings and keys Windows doesn't let us intercept
  (for example `Win+L` and `Ctrl+Alt+Del`).

### 6.2 Feedback

- **Indicator:** a small label ("Desktop 3", or the desktop's name) appears for about 1 second on the
  monitor that changed. It also appears at the ends of next/previous, and when an action isn't allowed.
- **Slide animation:** a horizontal slide on the switching monitor only, about 200 ms. Can be turned off.
  It never delays input. A new shortcut finishes the current animation immediately.
- **Desktop names:** set in the overview or the tray. Shown in the indicator, overview and tray menu.

### 6.3 Overview (`Win+Tab`)

Opens on every monitor at once. It shows:
- a strip of all desktops with live previews;
- the windows of the desktop shown on that monitor;
- "+" to add a desktop and "×" to remove one;
- drag and drop for windows (to any desktop) and desktops (to reorder);
- the ability to rename desktops.

`Esc` or `Win+Tab` closes it. Thumbnails are captured **only while the overview is open**, using DWM
thumbnails where possible.

### 6.4 Elevated (administrator) windows

Deskswap runs as a normal user, so Windows won't let it hide windows of apps running at a higher
integrity level (as administrator, or system processes). Such windows are detected by comparing the
process's integrity level with Deskswap's (a process whose level can't be read counts as higher) and are
left **unmanaged**: always visible, never moved. A one-time tray tip per session explains why. No option to
run Deskswap elevated for now.

## 7. Window eligibility

Deskswap manages a window only if **all** of these hold:
- top-level, visible, not owned by another window;
- not cloaked by DWM (`DWMWA_CLOAKED`), which excludes suspended UWP apps and windows on other
  Windows desktops;
- has a non-empty title;
- not a tool window, unless it has `WS_EX_APPWINDOW`;
- not a shell or system class: `Progman`, `WorkerW`, `Shell_TrayWnd`, `Shell_SecondaryTrayWnd`,
  `Windows.UI.Core.CoreWindow`, `XamlExplorerHostIslandWindow`, and others found by testing;
- not one of Deskswap's own windows;
- on the **current Windows desktop**. Windows cloaks windows on other Windows desktops, so the cloaked check
  already excludes them. The documented `IVirtualDesktopManager.IsWindowOnCurrentVirtualDesktop` is a fallback,
  used only if testing shows gaps. Users should keep a single Windows desktop.

Owned windows (dialogs, tool palettes) follow their owner automatically when the owner is hidden or shown.

## 8. Safety: never lose a window

1. **Write-ahead journal:** before hiding a window, append it (handle, pid, process start time, exe
   path, title) to `journal.json` and flush to disk. Remove it after it is shown again.
2. **Watchdog process:** a small helper started by Deskswap. If the main process dies unexpectedly,
   the watchdog shows every journaled window.
3. **Startup recovery:** on start, show every window left in the journal (checking pid + start time so
   a reused handle isn't touched), then rebuild state.
4. **Show all windows** (tray menu, and `Win+Ctrl+Shift+F12`): shows everything and pauses Deskswap
   until resumed from the tray menu or by pressing `Win+Ctrl+Shift+F12` again. While paused, every other
   shortcut is left to Windows.
5. **Exit, update, uninstall:** show every hidden window first.
6. **Any unexpected error** in a switch or move: show all windows of the affected monitor, log the
   error, and keep running. Never crash with windows hidden.

## 9. Coexisting with Windows

- Hidden windows disappear from the taskbar and `Alt+Tab`. This is expected: it is how per-monitor
  desktops look. For the best taskbar experience, recommend the Windows setting
  *Show my taskbar apps on → taskbar where window is open*.
- Deskswap takes over `Win+Ctrl+←/→`, `Win+Ctrl+D`, `Win+Ctrl+F4` and `Win+Tab`, so Windows' own
  desktops are effectively unused. Deskswap only manages windows on the current Windows desktop (§7).
- No undocumented COM interfaces. No injection into `explorer.exe`.

## 10. Persistence

All files live in `%LOCALAPPDATA%\Deskswap\` as JSON (System.Text.Json source generation), each with
a `schemaVersion`.

| File | Contents | Written |
|---|---|---|
| `settings.json` | Shortcuts, animation, indicator, language, restore period, start with Windows | On change |
| `rules.json` | User rules (§5.2) | On change |
| `layout.json` | Pool, monitor mapping, window snapshot (§5.3) | Debounced (≥ 2 s after the last change), and on exit |
| `journal.json` | Hidden windows (§8) | Before every hide, after every show |

- Writes are atomic: write a temp file, then replace.
- A corrupt file is renamed `*.corrupt-<timestamp>` and defaults are used, with a tray notice.
- New settings keys get defaults. Existing values, including shortcut bindings, are never reset by an upgrade.

## 11. Localization

- **English and Spanish** from the first release. The language follows the Windows display language,
  with an override in Settings.
- All UI strings live in `.resx` resources. Code, logs and docs are in English.

## 12. Performance targets

- Switching (hide + show + focus) finishes in under **50 ms**, not counting animation, with 30 windows.
- Idle CPU is about 0%. Deskswap is event-driven (WinEvent hooks). Its safety-net reconciliation scan
  runs every **5 s** at most and does no work when nothing changed.
- No disk writes while idle. No thumbnails unless the overview is open.
- Hook callbacks only enqueue work. All processing happens on the UI thread.

## 13. Edge-case checklist

Each item needs a Core unit test, or a manual smoke-test step for Platform behavior.

- [ ] Swap between two monitors with different resolutions or DPI. Windows are scaled and clamped.
- [ ] Next/previous skip desktops shown elsewhere and stop at the ends.
- [ ] Removing a shown desktop, an unshown one, and the last one allowed by invariant 3.
- [ ] Go to N when N doesn't exist.
- [ ] Monitor unplugged while its desktop has windows. They stay hidden and recoverable, and come back on reconnect.
- [ ] Laptop dock/undock changes the monitor count and every `\\.\DISPLAYn` name.
- [ ] Window handle reused by a new window after the old one closed. Never confuse the two.
- [ ] An app shows its own hidden window. Activation switches the desktop (§5.4).
- [ ] Minimized and maximized windows survive switches, swaps and travel.
- [ ] Owned dialogs and modal windows follow their owner.
- [ ] Elevated windows stay visible, with a one-time tip.
- [ ] An app that re-shows itself right after being hidden doesn't make two desktops ping-pong.
- [ ] Moving the focused window to the next desktop keeps it focused and on top.
- [ ] Cloaked, UWP and Store apps; apps whose window class changes; windows with an empty title.
- [ ] Full-screen apps and games. Don't break exclusive full screen.
- [ ] Deskswap killed with windows hidden. The watchdog restores them; so does a restart.
- [ ] Reboot. Layout restores within the restore period; a new window afterwards stays where it opened.
- [ ] Two windows of the same app with different titles restore to their own desktops.
- [ ] Corrupt `settings.json`. Backup and defaults; shortcuts aren't silently lost on upgrade.
- [ ] A shortcut already registered by another app. Report the conflict in Settings.

## 14. Milestones

| # | Scope | You can… |
|---|---|---|
| **M1** | Pool model with go to / next / previous / swap / add / remove; show/hide per monitor; eligibility; journal, watchdog and startup recovery; keyboard hook with default shortcuts; indicator; tray menu (Show all windows, Pause, Exit); restore pool and monitor mapping after a Deskswap restart; English and Spanish resources | Replace your current app for daily use |
| **M2** | Event-driven new-window placement; activation switches desktop; move-window shortcuts; pinning; elevated windows left unmanaged; monitor connect/disconnect; windows travel with their desktop | Work without ever placing windows by hand |
| **M3** | Rules; auto-remember and restore after reboot; settings window with shortcut editor; start with Windows; desktop names | Reboot and find everything where it was |
| **M4** | Overview (`Win+Tab`) with previews and drag and drop, add/remove/reorder; slide animation | Manage desktops visually |
| **M5** | Per-desktop wallpaper (per monitor, documented `IDesktopWallpaper`); installer, code signing, auto-update | Share it with others |

## 15. Deferred and open questions

- **Full-screen apps on their own desktop** (macOS behavior): deferred. Full-screen windows behave like normal windows for now.
- **Reordering desktops by most recent use** (a macOS option): not planned.
- **Running elevated to manage admin windows:** not planned (§6.4).
- **Distribution** (MSIX/Store vs installer), **code signing** and the **license** are decided before M5.
