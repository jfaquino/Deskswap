# Overview (`Win+Tab`) — UX specification

Status: v4 (2026-11-20). v4: wallpaper backdrop, desktop cards drawn from their windows, names that morph
into cards, reordering by dragging cards, a dragged window that shrinks near the strip. v3: the strip stays expanded once
reached, dragging never switches desktops, windows get captions and a larger macOS-style layout.
v2 added the macOS research (§10) and the decisions taken from it: a
collapsed desktop strip that expands on hover, the app windows view, edge drag, `Win+Ctrl+↑`, layout that
keeps windows in their places, and optional grouping by app.
Behavior rules (swap, remove, move) come from [design.md](design.md); this document defines how the
overview looks, moves and responds. Where they disagree, update both.

## 1. Goals

- **Glanceable:** in under a second, see every desktop and the windows on this monitor's desktop.
- **Direct:** everything the shortcuts do can be done by pointing, clicking and dragging.
- **Per monitor:** each monitor gets its own overview of the desktop it shows; all open together.
- **Native:** looks and feels like part of Windows 11 (Fluent), in light and dark.
- **Fast:** first frame in under 150 ms; never janky while dragging.

## 2. Opening and closing

| Opens with | Notes |
|---|---|
| `Win+Tab` | Toggles. Replaces Windows Task View while Deskswap runs (design §6.1). |
| `Win+Ctrl+↑` | Same as `Win+Tab` (macOS `Control+↑`). |
| Tray icon left-click | Right-click keeps the tray menu. |
| 4-finger swipe up | Via Windows' custom gesture → `Win+Tab` (documented in the help screen). |

- Opens on **every monitor at once**. Keyboard focus starts on the monitor under the mouse.
- Closes with `Esc`, `Win+Tab`, clicking empty space, or choosing a window or desktop. While it is open, Deskswap's
  keyboard hook owns `Esc` (cancel a rename or a drag, else close), so it works even if Windows didn't give the
  overview the keyboard. `Esc` is never taken from other apps while the overview is closed.
- While open, Deskswap's other shortcuts still work (e.g. `Win+Ctrl+→` switches and updates the overview).

## 3. Layout (per monitor)

```
┌────────────────────────────────── Monitor ──────────────────────────────────┐
│   ┌────┐ ┌────┐ ┌────┐ ┌────┐  ┌───┐                                         │
│   │ 1  │ │ 2● │ │ 3◐ │ │ 4  │  │ + │        desktop strip (all desktops)     │
│   └────┘ └────┘ └────┘ └────┘  └───┘                                         │
│    Mail   Work   Music                                                        │
│                                                                              │
│        ┌──────────────┐   ┌───────────────────┐   ┌──────────┐               │
│        │   Chrome     │   │     VS Code       │   │  Slack   │  windows of   │
│        └──────────────┘   └───────────────────┘   └──────────┘  this monitor's│
│                  ┌──────────┐   ┌──────┐                         desktop,     │
│                  │ Terminal │   │ 📌 Spotify │                    spread out   │
│                  └──────────┘   └──────┘                                      │
└──────────────────────────────────────────────────────────────────────────────┘
● shown on this monitor (accent outline)   ◐ shown on another monitor (monitor badge)
```

- **Backdrop:** the monitor's **wallpaper**, softly blurred and dimmed (as macOS has done since OS X Yosemite:
  the desktop picture shows through, without copies of the windows). If Windows doesn't report a wallpaper,
  the monitor's image is blurred instead.
- **Desktop strip** (top, centered), macOS-style in two states:
  - **Collapsed** (default): a slim row of desktop **names** ("Mail", "Work", "Desktop 4", …) and `+`; the
    one on this monitor is highlighted in the accent color, one shown on another monitor carries a small
    monitor badge. Clicking a name works like clicking its card.
  - **Expanded:** when the pointer reaches the strip, or a dragged window comes near it, it grows into
    thumbnail cards, and **stays expanded until the overview closes** (as macOS does: the bar only opens
    collapsed). Each card:
  - a preview of the desktop in miniature: its monitor's wallpaper with its windows drawn in their places and
    stacking order, each from the last picture Deskswap took of it on screen (its icon if never seen). It is
    rebuilt whenever the overview's state changes, so a window dropped on a desktop appears on its card at once.
  - the desktop's **name** below (or "Desktop N"), editable in place;
  - **state:** accent outline = shown on *this* monitor; a small monitor badge (e.g. "◐ Left") = shown on
    another monitor; nothing = hidden;
  - on hover: a round close button in the card's top-right corner (dark translucent, red on hover, as Windows
    close buttons), hidden when no desktop can be removed. A desktop shown on another monitor has a small
    monitor badge in its bottom-left corner.
- **Window area** (below the strip): the windows of this monitor's desktop, as large as the space allows, like
  macOS Mission Control. They keep their relative sizes and their order on screen: windows high on the screen
  go in the top rows, and left-to-right order is kept within a row (rows of similar width, the row count that
  gives the largest previews wins). Under every preview: the app icon and window title (always shown, so
  windows are easy to tell apart). The caption ends with a **pin button** (design §3.6): a filled accent pin on pinned windows, always shown; an
  outline pin on other windows while they are pointed at. Clicking it pins or unpins the window. Minimized
  windows are included, slightly dimmed.
- **Group by app** (setting, off by default, as on macOS): windows of the same app are placed next to each
  other with the app's icon and name above the group.
- **Empty desktop:** the window area shows "No windows on this desktop" in secondary text.

## 4. App windows view (`Win+Ctrl+↓`)

macOS App Exposé, extended to every desktop: shows **every window of the app you are using, on every
desktop**, on the monitor of the focused window.

- Grouped by desktop: a header per desktop ("Desktop 3 · Music", with its monitor badge if shown) and that
  desktop's windows of the app below it, laid out like the overview.
- Click a window: if its desktop is on screen (on any monitor), the window just gets focus there. If its
  desktop is hidden, that desktop is shown on the monitor that last showed it (where its windows are; this
  monitor if that one is gone), then the window gets focus. `Esc`, `Win+Ctrl+↓` or clicking empty space closes it.
- Arrow keys move between windows; `Enter` chooses.
- Opening it while the overview is open switches to it, and `Win+Tab` switches back.
- The app is the one of the window you were using; windows count as the same app when they run the same
  executable. The header shows the app's icon and name.
- Windows on screen have live previews; windows on hidden desktops show the last picture Deskswap took of
  them (their icon if never seen), so every window can be recognized.
- It is for picking a window: no dragging, menus or pin buttons here.

## 5. Interactions

| Action | Result |
|---|---|
| Click a window | Close the overview and focus that window. |
| Click a desktop card | This monitor shows that desktop (swap if another monitor shows it), then the overview closes. |
| Drag a window onto a desktop card | The window moves to that desktop (design §3.4). **The view never switches while dragging**: what each monitor shows changes only when you click a desktop. The card under the dragged window highlights. |
| Drag a window onto the `+` card | A new desktop is created with that window on it. |
| Drag a window into another monitor's window area | The window moves to the desktop shown there. |
| Drag a desktop card along the strip | Reorders desktops (design §3.2), as on macOS: the card follows the pointer and the others slide aside to show where it will land; a click without moving still shows the desktop. Names and windows go with it. |
| Drag a desktop card into another monitor's window area | That monitor shows it (swap if needed). |
| `×` on a card | Removes the desktop (design §3.2: its windows merge into the desktop that takes its place). Not allowed below one desktop per monitor — the button is disabled with a tooltip. |
| Double-click a card's name, or `F2` | Rename in place; `Enter` saves, `Esc` cancels; empty clears the name. |
| Click a window's pin button | Pins it to every desktop of its monitor, or unpins it (design §3.6). Clicking the rest of the caption picks the window. |
| Right-click a window | Menu: **Move to ▸** (desktops), **Pin to all desktops** / **Unpin**. |
| Right-click a card | Menu: **Rename**, **Remove**. |
| Point at the collapsed strip | It expands to thumbnails and stays expanded until the overview closes. |

**Outside the overview — edge drag** (macOS "drag a window to the screen edge"): while you drag a window by
its title bar against an **outer** screen edge (one with no monitor beyond it) and hold it there ~0.7 s, the
window moves to the previous/next free desktop and the monitor follows, like `Win+Ctrl+Shift+←/→`. Inner
edges keep moving windows between monitors as Windows normally does. A subtle edge glow shows the drop
will switch desktops. (Design §3.4.)

**Keyboard** (on the monitor that has focus; `Tab` moves focus between the strip and the window area):

| Key | Action |
|---|---|
| Arrow keys | Move the selection (windows in the area, cards in the strip) |
| `Enter` / `Space` | Activate the selection (focus window / show desktop) |
| `1`…`9` | Show desktop N on this monitor |
| `Ctrl+N` | Add a desktop |
| `Delete` | Remove the selected desktop card |
| `F2` | Rename the selected desktop |
| `P` | Pin or unpin the selected window |
| `Ctrl+←` / `Ctrl+→` | Move keyboard focus to the overview on the monitor to the left/right |
| `Esc` | Close |

## 6. Visual design (Fluent)

- **Theme:** follows Windows light/dark (`AppsUseLightTheme`) and the system **accent color**; high
  contrast themes use system colors and drop translucency.
- **Typography:** Segoe UI Variable. Card names: Body (14 px). Window titles: Caption (12 px).
  Empty-state text: Subtitle (20 px), secondary color.
- **Shape:** windows 8 px corner radius, cards 6 px, buttons 4 px.
- **Elevation:** window tiles shadow 8 (hover 16); cards shadow 4; dragged item shadow 28.
- **Selection/focus:** 2 px accent outline + 1 px inner contrast stroke (visible on any content).
- **Spacing:** 8 px grid; strip cards 12 px apart; window tiles at least 24 px apart.
- **Desktop indicator** (restyled, design §6.2): an Acrylic pill near the top center of the switching
  monitor showing the desktop name or number and a row of dots (one per desktop; filled = this one,
  ring = shown on another monitor). Fades after ~1 s.

## 7. Motion

All durations halve to fades if Windows "Animation effects" is off.

| Moment | Motion |
|---|---|
| Open | The first frame is the screen itself: wallpaper, and live previews exactly over their windows. Each window then shrinks to its tile (280 ms, ease-out) while the wallpaper blurs and dims and the taskbar fades; the strip slides down 12 px while fading in. Timed from the first frame drawn. |
| Close | Reverse (180 ms, ease-in "accelerate"). Choosing a window: it grows back to its real place; the others fade. |
| Hover | Tile scales to 1.03 and lifts to shadow 16 (120 ms). |
| Strip expand | Each name morphs into its card: the pill grows into the card's size and corners while the name fades out and the desktop picture fades in; names reappear under the cards (260 ms ease-out). The window area moves down in step. |
| Drag | The lifted window scales to 0.95 and follows the pointer; nearing the strip it shrinks smoothly toward card size so it is easy to drop on a card (as on macOS). Drop targets highlight (100 ms). |
| Switch desktop (in overview) | Window area cross-slides in the direction of the desktop's position (200 ms). |
| Switch desktop (no overview) | The monitor's content slides horizontally (≈200 ms) and the indicator pill appears. A new shortcut finishes the current animation instantly. |

## 8. Accessibility

- Fully usable with the keyboard; focus always visible.
- UI Automation names: "Desktop 3, Work, shown on this monitor", "Chrome — window, desktop 2".
- Respects text scaling, high contrast, and reduced motion.

## 9. Performance and technical notes

- **Live previews** use DWM thumbnails (`DwmRegisterThumbnail`): the compositor draws them, at no CPU
  cost to Deskswap. They exist for every window on screen. For most **minimized** windows Windows keeps no content
  (tested: it hands back a generic 200×108 stand-in, and by the time a minimize is reported even `PrintWindow`
  only gets a title bar), so a live preview is used only when its size matches the restored window. Otherwise,
  and for windows on hidden desktops, the tile shows the window's last picture; with no picture either, its icon.
- **Minimized windows** (setting, default *last content*): so that a window has a picture when it gets minimized,
  Deskswap takes a card-sized picture (360 px wide) of the window in use every 3 seconds, from the screen, in
  memory only. It skips the picture while the overview or an indicator is on screen, for windows partly off their
  monitor, and when nothing changed since the last one (same window, same place, no keyboard or mouse input). The
  other choice, *app icon*, takes no such pictures and shows minimized windows with their icon.
- **Previews follow their window:** a preview shows the window's visible frame (without its invisible resize
  borders), computed from the preview's own source size (`DwmQueryThumbnailSourceSize`), so it stays right for
  windows Windows scales for DPI. It is computed again whenever the window is minimized, restored, maximized or
  resized while the overview is open (window events refresh just that tile). A minimized window has no frame to
  measure, so the borders learned while it was on screen are used.
- **Desktop cards** are composed, not screenshots: wallpaper (decoded once, cached) plus each window's last
  picture, cut from a monitor image taken when the overview opens and when a desktop is hidden. Pictures are
  kept in memory only, dropped when their window closes, and never written to disk.
- **Memory stays flat:** when the overview closes, its windows drop every image, card and state they held, and
  window pictures are kept at card size (at most 360 px wide). Two seconds after closing, Deskswap runs one
  compacting garbage collection (large object heap included) and logs the result ("Memory after overview"),
  because an idle tray app otherwise rarely gives back the several megabytes of images each opening allocates.
  (Background garbage collection stays on: blocking collections would make animations stutter.)
- **First opening:** at startup each overview window is drawn once off-screen, so the first `Win+Tab` doesn't
  pay WPF's one-time costs (graphics setup, shaders, code compilation).
- **Window layout** (exposé) is computed in Core: pure, unit-tested, deterministic.
- **UI technology:** WPF on .NET 10 in its own project (`Deskswap.Ui`), one borderless per-monitor window.
  The backdrop is the monitor's image taken at opening, blurred and tinted (it looks like Acrylic and works on
  every monitor). The tray, hooks and engine stay as they are. The app owns the model: the overview shows an
  `OverviewState` and reports what the user wants through `IOverviewActions`.
- **What the compositor covers:** DWM thumbnails are drawn above the overview's own content, so anything that
  must appear over a preview (the dragged window) is a separate small window; captions go under previews, and the
  window area always starts below the strip (it moves down when the strip expands).
- **Smooth opening:** the backdrop is blurred once, on a small copy of the monitor's image, and faded in;
  no blur or shadow effects are re-rendered per frame. Animations are timed from the first frame drawn.
- **Smooth motion (open, close, re-layout):** only the live previews move, on a thread of their own that makes
  exactly one update per frame the compositor draws (`DwmFlush` paces it). Previews are drawn by the compositor,
  not WPF, so moving them from the UI thread tied them to WPF's frame timing and to whatever else that thread was
  doing, and WPF-drawn frames and shadows trailed them by a frame. While a preview moves, its tile's frame,
  shadow and caption wait at the destination, hidden, and fade in when it arrives (on close they don't come back:
  the real window takes its place). Small motions (hover) and tiles without a live preview still move on the UI
  thread. Each animation logs its frame times (frames, typical and worst gap, late frames, work per frame) for
  both threads, to compare machines and changes.
- **Screenshots and recordings see Deskswap:** its windows are not hidden from capture, so the overview and
  indicators can be shown in screenshots, recordings and screen sharing. Deskswap's own snapshots are taken only
  while its windows are hidden (the overview closes first and Deskswap waits for the compositor to draw a frame
  without it), or, for an indicator still on screen, hide it from capture only for that snapshot. While the
  overview is open, hiding a desktop keeps the pictures taken as it opened (nothing else was on screen since).
- Overview windows belong to Deskswap's process, so they are never managed as desktop windows.

## 10. macOS research and decisions

How Deskswap compares with macOS Mission Control and Spaces (Apple Support, current macOS guides):

| Area | macOS | Deskswap |
|---|---|---|
| Spaces bar | Collapsed to names, expands to thumbnails when the pointer reaches the top | Same (§3) |
| Add / remove / reorder desktops | `+` in the bar; `×` on hover; drag to reorder (numbers update) | Same, plus keyboard |
| Desktop names | Fixed "Desktop N"; renaming needs third-party apps | **Editable** |
| Switch desktops | Swipe, `Control+←/→`; `Control+1…9` exist but are **off by default** | `Win+Ctrl+←/→`, `Win+Ctrl+1…9` **on by default**, swipe via custom gesture |
| Move a window to another desktop | Drag in Mission Control, or to the screen edge; **no keyboard shortcut** | Drag in the overview, edge drag, **and** `Win+Ctrl+Shift+←/→/1…9` |
| Move a window to another display | Drag onto the other display's Spaces bar | Drag into the other monitor's overview, or `Win+Ctrl+Shift+N` |
| App windows | App Exposé (`Control+↓`): current app's windows | `Win+Ctrl+↓`, across **all** desktops (§4) |
| Group windows by app | Setting | Setting, off by default |
| Open Mission Control | `Control+↑`, F3, swipe up, hot corner, drag window to top edge | `Win+Tab`, `Win+Ctrl+↑`, tray click, swipe up. Not the top edge: on Windows it opens Snap layouts |
| Switch to an app's Space when activated | On by default | Same (design §5.4) |
| Assign app to desktop | Dock › Options › All Desktops / This Desktop / Desktop on Display / None | Rules (milestone 4) |
| Rearrange by recent use | Option; many users turn it off because numbers shift | Not planned |
| Full-screen apps as Spaces | Yes | Deferred (not M3) |
| Limit | 16 desktops | 16 desktops |

Decided from the prototype review: strip on top; clicking a desktop switches **and closes** (as macOS);
indicator with dots. After trying the first build: no spring-loaded switch while dragging (dragging only
moves windows), and the expanded strip stays open like macOS.

Sources: [Mission Control](https://support.apple.com/guide/mac-help/view-open-windows-spaces-mission-control-mh35798/mac),
[multiple Spaces](https://support.apple.com/guide/mac-help/work-in-multiple-spaces-mh14112/mac),
[Desktop & Dock settings](https://support.apple.com/guide/mac-help/change-desktop-dock-settings-mchlp1119/mac),
[keyboard shortcuts](https://support.apple.com/en-us/102650), [gestures](https://support.apple.com/en-us/102482),
[Split View](https://support.apple.com/guide/mac-help/use-apps-in-split-view-mchl4fbe2921/mac),
and community guides on the Spaces bar ([Mac Sales](https://eshop.macsales.com/blog/49131-tips-for-using-mission-control-on-a-mac/),
[MacMost](https://macmost.com/23-tips-for-using-desktop-spaces-on-your-mac.html)).
