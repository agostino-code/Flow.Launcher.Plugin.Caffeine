# Caffeine Plugin — Timed Mode Design Spec

**Date:** 2026-05-06
**Status:** Approved
**Target repo:** https://github.com/o850cHQk/Flow.Launcher.Plugin.Caffeine
**Current version:** 1.2.0
**Proposed version:** 1.3.0

---

## Summary

Add timed activation to the Flow Launcher Caffeine plugin. Users can keep their PC awake for a specific duration (30 minutes, 1 hour, etc.) instead of only toggling indefinitely. This brings feature parity with PowerToys Awake's timed mode, inside a tool Flow Launcher users already have.

---

## User Experience

### Flow Launcher Query — no argument (`caf`)

When caffeine is OFF, show the preset duration list:

```
Keep awake for 30 minutes
Keep awake for 1 hour
Keep awake for 2 hours
Keep awake for 5 hours
Keep awake for 8 hours
Keep awake indefinitely
```

When caffeine is ON (e.g. 1h 23m remaining), show status at top plus the full preset list:

```
Title:    Caffeine is active — 1h 23m remaining
Subtitle: Select to turn off
─────────
Keep awake for 30 minutes
Keep awake for 1 hour
Keep awake for 2 hours
Keep awake for 5 hours
Keep awake for 8 hours
Keep awake indefinitely
```

- Clicking the status row turns caffeine off. (Note: the Flow Launcher status row IS clickable, unlike the tray menu status label which is disabled/informational. This is intentional — Flow Launcher results are always clickable.)
- Clicking any duration row starts (or restarts) caffeine with that duration.

### Flow Launcher Query — with number (`caf 3`)

Bare number = hours. Shows a single result:

```
Keep awake for 3 hours
```

### Flow Launcher Query — with minutes (`caf 45m`)

Number followed by `m` = minutes. Shows a single result:

```
Keep awake for 45 minutes
```

### Flow Launcher Query — off (`caf off`)

If active: "Turn off Caffeine"
If inactive: "Caffeine is not active"

### Tray icon right-click context menu

```
Caffeine — 1h 23m remaining       (disabled label, shows status)
─────────────────
30 minutes
1 hour
2 hours
5 hours
8 hours
Indefinite
─────────────────
Turn Off
```

- Clicking a duration starts or restarts caffeine with that duration.
- Clicking "Turn Off" stops caffeine.
- Status label at top is not clickable — it's informational.

### Tray icon tooltip (hover)

Shows "Caffeine — 1h 23m remaining" or "Caffeine — Indefinite". Updates every 60 seconds.

### Timer expiry

When the countdown reaches zero:
- Caffeine deactivates (PowerUtilities.Shutdown is called)
- Tray icon disappears immediately
- Notification fires (if enabled): "Caffeine timer expired — system can now sleep."

### Auto-start behavior

The existing "Start with Flow Launcher" setting always starts caffeine in indefinite mode. No duration setting needed — if someone wants auto-start, they want it on for the whole session.

---

## Architecture

### Approach: Option B — CaffeineTimer as a new file

The existing codebase is intentionally compact. We respect that by keeping Main.cs as the plugin entry point and owner of start/stop logic, and extracting only the timer into its own class. This is additive — we're not restructuring existing code, just extending it.

### File changes

**New file: `CaffeineTimer.cs`**

A self-contained countdown clock. Responsibilities:
- Accept a duration and record the target end time as a `DateTime`
- Use `System.Timers.Timer` for the countdown
- Expose `GetRemainingTime()` returning a `TimeSpan?` (null = indefinite)
- Expose a formatted string method for display (e.g. "1h 23m remaining" or "Indefinite")
- Fire an `Expired` event when the countdown reaches zero
- Support restarting with a new duration (dispose old timer, create new one)
- Support indefinite mode (no timer, just tracks that caffeine is active without a deadline)
- Implement `IDisposable` for cleanup

The class knows nothing about Flow Launcher, tray icons, or PowerUtilities. It is purely a countdown clock.

**Important:** In indefinite mode, no CaffeineTimer instance is created. Main.cs holds a nullable reference (`CaffeineTimer? _timer`). When `_timer` is null, caffeine is either inactive or running indefinitely — the `IsActive` flag distinguishes these.

**Modified file: `Main.cs`**

Changes to the existing file:
- `StartCaffeine()` gains an optional `TimeSpan? duration` parameter
  - If duration is provided, creates/restarts `CaffeineTimer` with that duration
  - If null, starts in indefinite mode (same as current behavior)
- `StopCaffeine()` also disposes the timer
- Subscribe to `CaffeineTimer.Expired` event — when it fires, call `StopCaffeine()`
  - Must dispatch to UI thread since the timer fires on a background thread
- `Query()` method rewritten to:
  - Parse the input (empty, number, number+m, "off")
  - Return appropriate results based on current state
  - Show remaining time in subtitle when active
- `Dispose()` cleans up the timer

The existing `StartCaffeine`/`StopCaffeine` flow (PowerUtilities calls, tray icon show/hide, notifications) stays exactly as-is. The timer is layered on top.

**Modified file: `Tray/TrayIconManager.cs`**

- Accept a callback/delegate for duration selection and turn-off actions
- Expose a method to update the tooltip text (called every 60 seconds)

**Modified file: `Tray/TrayIconFactory.cs`**

- Populate the existing empty `ContextMenuStrip` with:
  - Status label (disabled/non-clickable)
  - Separator
  - Duration presets (30m, 1h, 2h, 5h, 8h, Indefinite)
  - Separator
  - Turn Off
- Each menu item's click handler calls back to Main.cs to start/restart/stop caffeine
- Accept parameters for the callback actions

**Unchanged files:**
- `Utilities/PowerUtilities.cs` — no changes needed
- `Utilities/NativeMethods.cs` — no changes needed
- `Settings/Settings.cs` — no changes needed (no new settings)
- `Settings/PluginSettings.xaml` — no changes needed
- `Settings/PluginSettings.xaml.cs` — no changes needed
- `plugin.json` — version bump from 1.2.0 to 1.3.0

---

## Design Decisions

**Fixed presets, not configurable:** 30m, 1h, 2h, 5h, 8h + Indefinite. Users can type any custom duration via the query (`caf 47m`), so configurability adds UI complexity without real benefit. Keeps the PR small and focused.

**Bare numbers default to hours:** `caf 3` = 3 hours. The typical use case for keeping a PC awake is hour-scale (presentations, downloads, long tasks). Minutes are available via `m` suffix.

**No Default Duration setting:** Auto-start is always indefinite. Eliminates a settings UI element and avoids the confusing scenario where caffeine silently expires during an auto-start session.

**Store end time, not remaining time:** The timer records `DateTime.Now + duration` as the expiry target. When displaying remaining time, it calculates `_expiresAt - DateTime.Now`. This avoids drift — no matter when you check, the remaining time is accurate.

**60-second tooltip refresh:** A lightweight `System.Timers.Timer` updates the tray icon tooltip text every 60 seconds. Since remaining time displays in hours and minutes (not seconds), a 1-minute refresh is imperceptible.

**Timer expiry calls StopCaffeine on UI thread:** The `System.Timers.Timer` elapsed event fires on a thread pool thread. Since StopCaffeine interacts with the tray icon (which runs on its own STA thread), we need to dispatch carefully. The existing TrayIconManager already handles cross-thread cleanup via `ContextMenuStrip.Invoke()`, so this pattern is established.

**Switching durations doesn't restart PowerUtilities:** If caffeine is already active and the user picks a new duration, we only reset the timer — no need to call `PowerUtilities.Shutdown()` and `PreventPowerSave()` again. The execution state flag is already set.

**Tooltip refresh timer is owned by TrayIconManager:** TrayIconManager starts a 60-second `System.Timers.Timer` in `ShowTray()` and stops it in `HideTray()`. Each tick, it pulls the current remaining-time string via a `Func<string>` delegate passed in from Main.cs. This keeps TrayIconManager unaware of CaffeineTimer — it just calls a function that returns a string.

---

## State Transitions

Six transitions the implementation must handle:

| From | To | What happens |
|------|----|-------------|
| Inactive | Timed | Call `PreventPowerSave()`, create new `CaffeineTimer` with duration, subscribe to `Expired`, show tray, notify |
| Inactive | Indefinite | Call `PreventPowerSave()`, no timer created (`_timer` stays null), show tray, notify |
| Timed | Timed (new duration) | Restart `CaffeineTimer` with new duration. Do NOT call `PreventPowerSave()` or `Shutdown()` again. Update tray menu status label. |
| Timed | Indefinite | Dispose `CaffeineTimer`, set `_timer = null`. Do NOT call `PreventPowerSave()` or `Shutdown()`. Update tray menu status label. |
| Indefinite | Timed | Create new `CaffeineTimer` with duration, subscribe to `Expired`. Do NOT call `PreventPowerSave()` or `Shutdown()`. Update tray menu status label. |
| Any active | Inactive | Call `Shutdown()`, dispose `CaffeineTimer` if it exists, hide tray, notify. Triggered by user action OR timer expiry. |

---

## Query Parsing Logic

```
input = query.Search.Trim()

if empty                              → show preset list (+ status if active)
if "off" (case-insensitive)           → show turn-off result
if number + "m"/"M" suffix            → interpret as minutes
if bare number (with optional "h"/"H") → interpret as hours
else                                  → show "invalid input" hint
```

Validation: reject zero, negative, or unreasonably large durations (e.g. > 24 hours). Show a friendly message like "Duration must be between 1 minute and 24 hours."

Edge cases:
- Decimals are supported: `caf 1.5` = 1.5 hours (90 minutes), `caf 1.5m` = 1.5 minutes (rounded to 2 minutes)
- `caf 3h` is valid and equivalent to `caf 3` (the `h` suffix is optional)
- Leading/trailing whitespace is trimmed; internal whitespace like `caf 3 m` is not supported (treated as invalid input)

---

## Remaining Time Display Format

- Under 1 hour: "42m remaining"
- 1 hour or more: "1h 23m remaining"
- Indefinite: "Indefinite"
- Expired/inactive: not shown

---

## Thread Safety Considerations

- `CaffeineTimer.Expired` fires on a thread pool thread. Main.cs must dispatch `StopCaffeine()` to the appropriate context.
- The tray icon runs on its own STA thread. Menu item clicks fire on that thread. Calls back to Main.cs (to start/restart caffeine) cross thread boundaries — PowerUtilities.PreventPowerSave() already handles this by spawning its own long-running task.
- `CaffeineTimer` should use a lock around start/stop/restart to prevent race conditions if the timer expires at the same moment the user picks a new duration.

---

## Testing Checklist

- `caf` with no argument shows preset duration list
- `caf 5` activates for 5 hours
- `caf 30m` activates for 30 minutes
- `caf off` deactivates
- `caf` while active shows remaining time + turn off option + preset list
- Selecting a new duration while active restarts the timer (no PowerUtilities restart)
- Timer expiry auto-deactivates and fires notification
- Tray icon right-click shows context menu with status, durations, turn off
- Tray icon tooltip shows remaining time, updates every 60 seconds
- Tray context menu duration selection works (both when active and inactive)
- Auto-start with Flow Launcher uses indefinite mode
- Existing toggle behavior still works (indefinite = no timer)
- Plugin dispose cleans up timer and tray icon
- No crashes from timer thread calling UI operations
- Invalid input (e.g. `caf abc`, `caf -5`, `caf 0`) shows friendly error
- Edge case: timer expires while user has Flow Launcher open showing caffeine results

---

## PR Strategy

1. Open an issue first to propose the feature and get maintainer buy-in
2. Fork the repo, create a `feature/timed-mode` branch
3. Keep commits focused and clean
4. Bump version in plugin.json to 1.3.0
5. Write a clear PR description with the user experience section above
6. Reference the issue number in the PR
