# Caffeine Timed Mode — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add duration-based activation to the Flow Launcher Caffeine plugin so users can keep their PC awake for a specific time (30m, 1h, 2h, 5h, 8h, or custom) in addition to the existing indefinite toggle.

**Architecture:** New `CaffeineTimer.cs` class owns countdown logic. `Main.cs` gains query parsing and timer integration. `Tray/` files gain a context menu with duration presets and a 60-second tooltip refresh. No changes to `PowerUtilities`, `Settings`, or the settings UI.

**Tech Stack:** C# / .NET 7.0-windows, Flow.Launcher.Plugin v4.4.0, WinForms (NotifyIcon/ContextMenuStrip), WPF (settings panel, unchanged)

**Spec:** `docs/superpowers/specs/2026-05-06-caffeine-timed-mode-design.md`

---

## File Map

| File | Action | Responsibility |
|------|--------|---------------|
| `CaffeineTimer.cs` | **Create** | Countdown clock: start, restart, remaining time, expiry event |
| `Main.cs` | **Modify** | Query parsing, timer integration, state transitions, duration formatting |
| `Tray/TrayIconFactory.cs` | **Modify** | Build context menu items (status, presets, turn off) |
| `Tray/TrayIconManager.cs` | **Modify** | Tooltip refresh timer, status updates, callback plumbing |
| `Tray/TrayIconThreadManager.cs` | No change | |
| `plugin.json` | **Modify** | Version bump 1.2.0 → 1.3.0 |
| `Readme.md` | **Modify** | Document new query syntax and tray menu |
| `Utilities/PowerUtilities.cs` | No change | |
| `Settings/*` | No change | |

---

## Task 0: Install .NET 7 SDK

The project targets `net7.0-windows` but this machine has no .NET SDK installed (only runtimes). We need the SDK to build.

**Files:** None (system setup)

- [ ] **Step 1: Install .NET 7 SDK**

Run this in PowerShell to install via winget:

```powershell
winget install Microsoft.DotNet.SDK.7 --accept-source-agreements --accept-package-agreements
```

If winget is not available, download the .NET 7 SDK installer from the Microsoft .NET downloads page (search for ".NET 7.0 SDK" — pick the Windows x64 installer).

- [ ] **Step 2: Verify installation**

Close and reopen your terminal, then run:

```powershell
dotnet --list-sdks
```

Expected: A line like `7.0.xxx [C:\Program Files\dotnet\sdk]` appears in the output.

- [ ] **Step 3: Verify the project builds in its current state**

```powershell
dotnet build Flow.Launcher.Plugin.Caffeine.csproj
```

Expected: `Build succeeded. 0 Warning(s) 0 Error(s)` (some XML doc warnings are OK).

---

## Task 1: Create CaffeineTimer.cs

A self-contained countdown clock. Knows nothing about Flow Launcher, tray icons, or power management. It starts a timer, tracks the expiry time, reports remaining time, and fires an event when done.

**Files:**
- Create: `CaffeineTimer.cs`

- [ ] **Step 1: Create the file**

Create `CaffeineTimer.cs` in the project root (same directory as `Main.cs`):

```csharp
using System;
using System.Timers;

namespace Flow.Launcher.Plugin.Caffeine;

/// <summary>
/// Countdown timer for timed caffeine activation.
/// </summary>
internal class CaffeineTimer : IDisposable
{
    private Timer _timer;
    private readonly object _lock = new();

    /// <summary>
    /// The absolute time when caffeine will expire.
    /// </summary>
    public DateTime ExpiresAt { get; private set; }

    /// <summary>
    /// Fired when the countdown reaches zero. Fires on a thread pool thread.
    /// </summary>
    public event EventHandler Expired;

    /// <summary>
    /// Start or restart the countdown with a new duration.
    /// </summary>
    public void Start(TimeSpan duration)
    {
        lock (_lock)
        {
            _timer?.Stop();
            _timer?.Dispose();

            ExpiresAt = DateTime.Now + duration;
            _timer = new Timer(duration.TotalMilliseconds);
            _timer.Elapsed += OnTimerElapsed;
            _timer.AutoReset = false;
            _timer.Start();
        }
    }

    /// <summary>
    /// Get the time remaining until expiry.
    /// </summary>
    public TimeSpan GetRemainingTime()
    {
        var remaining = ExpiresAt - DateTime.Now;
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }

    /// <summary>
    /// Format remaining time for display. Example: "1h 23m remaining" or "42m remaining".
    /// </summary>
    public string FormatRemainingTime()
    {
        var remaining = GetRemainingTime();
        if (remaining <= TimeSpan.Zero)
            return "0m remaining";
        if (remaining.TotalHours >= 1)
            return $"{(int)remaining.TotalHours}h {remaining.Minutes}m remaining";
        return $"{(int)remaining.TotalMinutes}m remaining";
    }

    private void OnTimerElapsed(object sender, ElapsedEventArgs e)
    {
        Expired?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_timer != null)
            {
                _timer.Stop();
                _timer.Dispose();
                _timer = null;
            }
        }
    }
}
```

- [ ] **Step 2: Verify it compiles**

```powershell
dotnet build Flow.Launcher.Plugin.Caffeine.csproj
```

Expected: Build succeeded.

- [ ] **Step 3: Commit**

```powershell
git add CaffeineTimer.cs
git commit -m "feat: add CaffeineTimer countdown clock"
```

---

## Task 2: Modify TrayIconFactory — add context menu items

The factory currently creates an empty ContextMenuStrip. We'll populate it with a status label, duration presets, and a turn-off button. Menu item clicks dispatch to a thread pool thread via `Task.Run` to avoid deadlocking the tray's STA thread.

**Files:**
- Modify: `Tray/TrayIconFactory.cs`

- [ ] **Step 1: Rewrite TrayIconFactory.cs**

Replace the entire contents of `Tray/TrayIconFactory.cs` with:

```csharp
using System;
using System.Drawing;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Flow.Launcher.Plugin.Caffeine.Tray;

/// <summary>
/// Factory class for creating NotifyIcon instances.
/// </summary>
internal static class TrayIconFactory
{
    /// <summary>
    /// Creates a new caffeine NotifyIcon with a fully populated context menu.
    /// </summary>
    /// <param name="context">The plugin initialization context</param>
    /// <param name="onDurationSelected">Callback when a duration preset is clicked (null = indefinite)</param>
    /// <param name="onTurnOff">Callback when Turn Off is clicked</param>
    /// <returns>The configured NotifyIcon and the status menu item for later updates</returns>
    public static (NotifyIcon notifyIcon, ToolStripMenuItem statusItem) CreateCaffeineIcon(
        PluginInitContext context,
        Action<TimeSpan?> onDurationSelected,
        Action onTurnOff)
    {
        var notifyIcon = new NotifyIcon();
        var contextMenu = new ContextMenuStrip();

        var iconPath = Path.Combine(context.CurrentPluginMetadata.PluginDirectory, "Images/icon.ico");
        if (File.Exists(iconPath))
            notifyIcon.Icon = new Icon(iconPath);

        // Status label (non-clickable, updated by TrayIconManager)
        var statusItem = new ToolStripMenuItem("Caffeine is active") { Enabled = false };
        contextMenu.Items.Add(statusItem);
        contextMenu.Items.Add(new ToolStripSeparator());

        // Duration presets — Task.Run prevents deadlock when callback hides the tray from the STA thread
        var presets = new (string label, TimeSpan? duration)[]
        {
            ("30 minutes", TimeSpan.FromMinutes(30)),
            ("1 hour", TimeSpan.FromHours(1)),
            ("2 hours", TimeSpan.FromHours(2)),
            ("5 hours", TimeSpan.FromHours(5)),
            ("8 hours", TimeSpan.FromHours(8)),
            ("Indefinite", null),
        };

        foreach (var (label, duration) in presets)
        {
            var d = duration;
            contextMenu.Items.Add(new ToolStripMenuItem(label, null, (s, e) => Task.Run(() => onDurationSelected(d))));
        }

        contextMenu.Items.Add(new ToolStripSeparator());
        contextMenu.Items.Add(new ToolStripMenuItem("Turn Off", null, (s, e) => Task.Run(onTurnOff)));

        notifyIcon.Text = "Caffeine is active";
        notifyIcon.ContextMenuStrip = contextMenu;
        notifyIcon.Visible = false;

        return (notifyIcon, statusItem);
    }
}
```

**Key design decisions in this code:**
- `Task.Run(() => ...)` wraps every callback. Without this, clicking "Turn Off" would call `StopCaffeine` → `TrayIconManager.HideTray` → `TrayIconThreadManager.Dispose` → `Application.ExitThread`, all from the tray's own STA thread, which would deadlock on `_trayThread.Join(200)`. `Task.Run` moves the callback to a thread pool thread, matching how Flow Launcher's main thread calls these methods.
- The return type is now a tuple `(NotifyIcon, ToolStripMenuItem)` so TrayIconManager can update the status label text later.
- `statusItem.Enabled = false` makes it non-clickable (grayed out text, informational only).

- [ ] **Step 2: Verify it compiles**

```powershell
dotnet build Flow.Launcher.Plugin.Caffeine.csproj
```

Expected: Build fails — `TrayIconManager.cs` still calls the old single-argument `CreateCaffeineIcon`. That's expected; we fix it in Task 3.

- [ ] **Step 3: Commit**

```powershell
git add Tray/TrayIconFactory.cs
git commit -m "feat: populate tray context menu with duration presets"
```

---

## Task 3: Modify TrayIconManager — tooltip refresh and callback plumbing

TrayIconManager gains new parameters for callbacks and a status text provider. It owns a 60-second refresh timer that updates the tooltip and status menu item. It also exposes a public `UpdateStatus()` method for immediate refreshes when the user changes duration.

**Files:**
- Modify: `Tray/TrayIconManager.cs`

- [ ] **Step 1: Rewrite TrayIconManager.cs**

Replace the entire contents of `Tray/TrayIconManager.cs` with:

```csharp
using System;
using System.Windows.Forms;
using Timer = System.Timers.Timer;

namespace Flow.Launcher.Plugin.Caffeine.Tray;

/// <summary>
/// Manages the system tray icon for the Caffeine plugin.
/// </summary>
public static class TrayIconManager
{
    private static TrayIconThreadManager _threadManager;
    private static readonly object _lock = new();
    private static volatile bool _isVisible = false;

    private static NotifyIcon _notifyIcon;
    private static ToolStripMenuItem _statusItem;
    private static Func<string> _getStatusText;
    private static Timer _refreshTimer;

    /// <summary>
    /// Shows the caffeine tray icon in a separate background thread.
    /// </summary>
    /// <param name="context">The plugin initialization context</param>
    /// <param name="onDurationSelected">Callback when a duration preset is clicked</param>
    /// <param name="onTurnOff">Callback when Turn Off is clicked</param>
    /// <param name="getStatusText">Returns current status text (e.g. "1h 23m remaining" or "Indefinite")</param>
    public static void ShowTray(
        PluginInitContext context,
        Action<TimeSpan?> onDurationSelected,
        Action onTurnOff,
        Func<string> getStatusText)
    {
        lock (_lock)
        {
            if (_isVisible) return;

            _isVisible = true;
            _getStatusText = getStatusText;
            _threadManager = new TrayIconThreadManager();

            var (notifyIcon, statusItem) = TrayIconFactory.CreateCaffeineIcon(context, onDurationSelected, onTurnOff);
            _notifyIcon = notifyIcon;
            _statusItem = statusItem;

            RefreshStatusText();
            _threadManager.StartTrayThread(notifyIcon);
            StartRefreshTimer();
        }
    }

    /// <summary>
    /// Hides the caffeine tray icon and stops the thread.
    /// </summary>
    public static void HideTray()
    {
        lock (_lock)
        {
            if (!_isVisible) return;

            _isVisible = false;
            StopRefreshTimer();
            _threadManager?.Dispose();
            _threadManager = null;
            _notifyIcon = null;
            _statusItem = null;
            _getStatusText = null;
        }
    }

    /// <summary>
    /// Immediately refresh the tooltip and status menu item text.
    /// Call this when the duration changes so the user sees the update right away.
    /// </summary>
    public static void UpdateStatus()
    {
        lock (_lock)
        {
            if (!_isVisible) return;
            RefreshStatusText();
        }
    }

    private static void StartRefreshTimer()
    {
        _refreshTimer = new Timer(60_000);
        _refreshTimer.Elapsed += (s, e) => UpdateStatus();
        _refreshTimer.AutoReset = true;
        _refreshTimer.Start();
    }

    private static void StopRefreshTimer()
    {
        if (_refreshTimer != null)
        {
            _refreshTimer.Stop();
            _refreshTimer.Dispose();
            _refreshTimer = null;
        }
    }

    /// <summary>
    /// Updates the tooltip and status menu item by dispatching to the tray's STA thread.
    /// </summary>
    private static void RefreshStatusText()
    {
        var statusText = _getStatusText?.Invoke();
        if (statusText == null || _notifyIcon == null) return;

        var tooltipText = $"Caffeine — {statusText}";
        var menuText = $"Caffeine — {statusText}";

        try
        {
            if (_notifyIcon.ContextMenuStrip?.IsHandleCreated == true)
            {
                _notifyIcon.ContextMenuStrip.Invoke(new Action(() =>
                {
                    _notifyIcon.Text = tooltipText;
                    if (_statusItem != null)
                        _statusItem.Text = menuText;
                }));
            }
            else
            {
                _notifyIcon.Text = tooltipText;
                if (_statusItem != null)
                    _statusItem.Text = menuText;
            }
        }
        catch
        {
            // Icon may be disposing; safe to ignore
        }
    }
}
```

**Key design decisions:**
- `RefreshStatusText()` checks `IsHandleCreated` before calling `Invoke`. When the tray icon is first created but the STA message loop hasn't started yet, the handle doesn't exist and `Invoke` would throw. In that case we set the properties directly (safe since no other thread is competing yet).
- `UpdateStatus()` is public so Main.cs can trigger an immediate refresh when the user switches duration. The 60-second timer handles the periodic refresh for the tooltip hover.
- `StopRefreshTimer()` is called in `HideTray()` before disposing the thread manager to prevent the timer from firing during cleanup.
- The lock in `UpdateStatus` prevents races with `HideTray` (timer callback could fire while hide is in progress).

- [ ] **Step 2: Verify it compiles**

```powershell
dotnet build Flow.Launcher.Plugin.Caffeine.csproj
```

Expected: Build fails — `Main.cs` still calls `TrayIconManager.ShowTray(_context)` with the old single-argument signature. That's expected; we fix it in Task 4.

- [ ] **Step 3: Commit**

```powershell
git add Tray/TrayIconManager.cs
git commit -m "feat: add tooltip refresh timer and callback plumbing to TrayIconManager"
```

---

## Task 4: Modify Main.cs — query parsing, timer integration, state transitions

This is the big one. Main.cs gains:
- A nullable `CaffeineTimer` field
- Modified `StartCaffeine` that accepts an optional duration and handles all six state transitions
- Modified `StopCaffeine` with a `timerExpired` flag for different notification text
- New `Query` method with input parsing and preset list
- Helper methods for formatting durations and getting status text

**Files:**
- Modify: `Main.cs`

- [ ] **Step 1: Rewrite Main.cs**

Replace the entire contents of `Main.cs` with:

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using Flow.Launcher.Plugin.Caffeine.Settings;
using Flow.Launcher.Plugin.Caffeine.Tray;
using Flow.Launcher.Plugin.Caffeine.Utilities;

namespace Flow.Launcher.Plugin.Caffeine;

/// <summary>
/// Main plugin class for the Caffeine Flow Launcher plugin
/// </summary>
public class Caffeine : IPlugin, ISettingProvider, IDisposable
{
    internal static bool IsActive { get; private set; } = false;

    private PluginInitContext _context;
    private Settings.Settings _settings;
    private string _iconPath;
    private CaffeineTimer _timer;

    private static readonly (string label, TimeSpan? duration)[] Presets =
    {
        ("30 minutes", TimeSpan.FromMinutes(30)),
        ("1 hour", TimeSpan.FromHours(1)),
        ("2 hours", TimeSpan.FromHours(2)),
        ("5 hours", TimeSpan.FromHours(5)),
        ("8 hours", TimeSpan.FromHours(8)),
        ("indefinitely", null),
    };

    /// <summary>
    /// Initialize the plugin
    /// </summary>
    /// <param name="context">Plugin initialization context</param>
    public void Init(PluginInitContext context)
    {
        _context = context;
        _settings = context.API.LoadSettingJsonStorage<Settings.Settings>();
        _iconPath = Path.Combine(context.CurrentPluginMetadata.PluginDirectory, "Images/icon.png");

        if (_settings.StartWithFlowLauncher)
        {
            StartCaffeine();
        }
    }

    /// <summary>
    /// Query method for Flow Launcher
    /// </summary>
    public List<Result> Query(Query query)
    {
        var results = new List<Result>();
        var input = query.Search.Trim();

        if (string.IsNullOrEmpty(input))
            return BuildPresetResults(results);

        if (input.Equals("off", StringComparison.OrdinalIgnoreCase))
            return BuildOffResult(results);

        return BuildCustomDurationResult(results, input);
    }

    private List<Result> BuildPresetResults(List<Result> results)
    {
        if (IsActive)
        {
            results.Add(new Result
            {
                Title = $"Caffeine is active — {GetStatusText()}",
                SubTitle = "Select to turn off",
                Action = c => { StopCaffeine(); return true; },
                IcoPath = _iconPath
            });
        }

        foreach (var (label, duration) in Presets)
        {
            var d = duration;
            results.Add(new Result
            {
                Title = $"Keep awake for {label}",
                SubTitle = IsActive ? "Will restart with new duration" : "",
                Action = c => { StartCaffeine(d); return true; },
                IcoPath = _iconPath
            });
        }

        return results;
    }

    private List<Result> BuildOffResult(List<Result> results)
    {
        if (IsActive)
        {
            results.Add(new Result
            {
                Title = "Turn off Caffeine",
                SubTitle = $"Currently active — {GetStatusText()}",
                Action = c => { StopCaffeine(); return true; },
                IcoPath = _iconPath
            });
        }
        else
        {
            results.Add(new Result
            {
                Title = "Caffeine is not active",
                SubTitle = "Nothing to turn off",
                Action = c => false,
                IcoPath = _iconPath
            });
        }

        return results;
    }

    private List<Result> BuildCustomDurationResult(List<Result> results, string input)
    {
        var parsed = ParseDuration(input);
        if (parsed.HasValue)
        {
            var duration = parsed.Value;
            results.Add(new Result
            {
                Title = $"Keep awake for {FormatDurationLabel(duration)}",
                SubTitle = IsActive ? "Will restart with new duration" : "",
                Action = c => { StartCaffeine(duration); return true; },
                IcoPath = _iconPath
            });
        }
        else
        {
            results.Add(new Result
            {
                Title = "Invalid duration",
                SubTitle = "Try a number (hours) or number + m (minutes). Example: 3 or 45m",
                Action = c => false,
                IcoPath = _iconPath
            });
        }

        return results;
    }

    /// <summary>
    /// Parse user input into a TimeSpan. Returns null if input is invalid.
    /// Supports: "45m" (minutes), "3" or "3h" (hours), "1.5" (1.5 hours = 90 min).
    /// </summary>
    internal static TimeSpan? ParseDuration(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return null;

        if (input.EndsWith("m", StringComparison.OrdinalIgnoreCase))
        {
            if (double.TryParse(input.AsSpan(0, input.Length - 1), out var minutes)
                && minutes > 0 && minutes <= 1440)
            {
                var rounded = Math.Max(1, (int)Math.Ceiling(minutes));
                return TimeSpan.FromMinutes(rounded);
            }
            return null;
        }

        var numStr = input.EndsWith("h", StringComparison.OrdinalIgnoreCase)
            ? input.AsSpan(0, input.Length - 1)
            : input.AsSpan();

        if (double.TryParse(numStr, out var hours) && hours > 0 && hours <= 24)
            return TimeSpan.FromHours(hours);

        return null;
    }

    /// <summary>
    /// Format a TimeSpan as a human-readable label. Example: "1 hour", "45 minutes", "2h 30m".
    /// </summary>
    internal static string FormatDurationLabel(TimeSpan duration)
    {
        if (duration.TotalHours >= 1)
        {
            var hours = (int)duration.TotalHours;
            var minutes = duration.Minutes;
            if (minutes == 0)
                return hours == 1 ? "1 hour" : $"{hours} hours";
            return $"{hours}h {minutes}m";
        }
        var mins = (int)Math.Ceiling(duration.TotalMinutes);
        return mins == 1 ? "1 minute" : $"{mins} minutes";
    }

    private string GetStatusText()
    {
        if (!IsActive) return "Inactive";
        if (_timer == null) return "Indefinite";
        return _timer.FormatRemainingTime();
    }

    /// <summary>
    /// Start caffeine, or change the active duration if already running.
    /// Pass null for indefinite mode.
    /// </summary>
    private void StartCaffeine(TimeSpan? duration = null)
    {
        if (!IsActive)
        {
            // Transition: Inactive → Timed or Inactive → Indefinite
            PowerUtilities.PreventPowerSave();
            IsActive = true;

            if (duration.HasValue)
            {
                _timer = new CaffeineTimer();
                _timer.Expired += OnTimerExpired;
                _timer.Start(duration.Value);
            }

            if (_settings.ShowTrayIcon)
                TrayIconManager.ShowTray(_context, OnDurationSelected, () => StopCaffeine(), GetStatusText);
            if (_settings.SendNotifications)
                _context.API.ShowMsg("Caffeine - Flow Launcher ☕", "Caffeine is now active 🟢", _iconPath);
        }
        else
        {
            // Transition: Timed → Timed, Timed → Indefinite, or Indefinite → Timed
            if (duration.HasValue)
            {
                if (_timer == null)
                {
                    _timer = new CaffeineTimer();
                    _timer.Expired += OnTimerExpired;
                }
                _timer.Start(duration.Value);
            }
            else
            {
                _timer?.Dispose();
                _timer = null;
            }
            TrayIconManager.UpdateStatus();
        }
    }

    /// <summary>
    /// Stop caffeine and clean up timer.
    /// </summary>
    private void StopCaffeine(bool timerExpired = false)
    {
        if (IsActive)
        {
            PowerUtilities.Shutdown();
            IsActive = false;

            if (_timer != null)
            {
                _timer.Dispose();
                _timer = null;
            }

            if (_settings.ShowTrayIcon) TrayIconManager.HideTray();
            if (_settings.SendNotifications)
            {
                var message = timerExpired
                    ? "Caffeine timer expired — system can now sleep ⏰"
                    : "Caffeine is now inactive 🔴";
                _context.API.ShowMsg("Caffeine - Flow Launcher ☕", message, _iconPath);
            }
        }
    }

    private void OnTimerExpired(object sender, EventArgs e)
    {
        StopCaffeine(timerExpired: true);
    }

    private void OnDurationSelected(TimeSpan? duration)
    {
        StartCaffeine(duration);
    }

    /// <summary>
    /// Create the settings panel for the plugin
    /// </summary>
    public System.Windows.Controls.Control CreateSettingPanel()
    {
        return new PluginSettings(_context, _settings);
    }

    /// <summary>
    /// Dispose of resources when the plugin is unloaded
    /// </summary>
    public void Dispose()
    {
        StopCaffeine();
    }
}
```

**Key design decisions in this code:**

State transitions (from the spec):
- **Inactive → Timed:** `StartCaffeine(duration)` when `!IsActive` and `duration.HasValue` — calls `PreventPowerSave()`, creates timer, shows tray
- **Inactive → Indefinite:** `StartCaffeine(null)` when `!IsActive` — calls `PreventPowerSave()`, no timer, shows tray
- **Timed → Timed (new):** `StartCaffeine(duration)` when `IsActive` and `duration.HasValue` and `_timer != null` — restarts timer only
- **Timed → Indefinite:** `StartCaffeine(null)` when `IsActive` and `_timer != null` — disposes timer
- **Indefinite → Timed:** `StartCaffeine(duration)` when `IsActive` and `_timer == null` — creates new timer
- **Any active → Inactive:** `StopCaffeine()` — calls `Shutdown()`, disposes timer, hides tray

Thread safety:
- `OnTimerExpired` fires on a thread pool thread (from `System.Timers.Timer`). It calls `StopCaffeine` which calls `TrayIconManager.HideTray`. The existing `TrayIconThreadManager.CleanupTrayIcon` dispatches to the STA thread via `Invoke`, so this works correctly.
- `OnDurationSelected` is called from a thread pool thread (because `TrayIconFactory` wraps callbacks in `Task.Run`), so it avoids the STA deadlock.
- `ParseDuration` and `FormatDurationLabel` are `internal static` — pure functions, no state, safe from any thread.

- [ ] **Step 2: Verify the full project compiles**

```powershell
dotnet build Flow.Launcher.Plugin.Caffeine.csproj
```

Expected: Build succeeded. All four modified/created files compile together.

- [ ] **Step 3: Commit**

```powershell
git add Main.cs
git commit -m "feat: add query parsing, timer integration, and state transitions to Main.cs"
```

---

## Task 5: Update plugin.json and Readme.md

Bump the version and document the new features.

**Files:**
- Modify: `plugin.json`
- Modify: `Readme.md`

- [ ] **Step 1: Bump version in plugin.json**

Change line 7 of `plugin.json`:

```json
"Version": "1.3.0",
```

(Was `"1.2.0"`)

- [ ] **Step 2: Update Readme.md**

Add a "Timed Mode" section to the existing `Readme.md`. Insert it after the existing usage description. The exact content:

```markdown
### Timed Mode

Keep your PC awake for a specific duration instead of indefinitely:

| Command | Effect |
|---------|--------|
| `caf` | Show duration presets (30m, 1h, 2h, 5h, 8h, indefinite) |
| `caf 3` | Keep awake for 3 hours |
| `caf 45m` | Keep awake for 45 minutes |
| `caf 1.5` | Keep awake for 1.5 hours (90 minutes) |
| `caf off` | Turn off caffeine |

When caffeine is active with a timer, the remaining time is shown in the query results and tray icon tooltip.

Right-click the tray icon for quick access to duration presets and turn off.
```

- [ ] **Step 3: Commit**

```powershell
git add plugin.json Readme.md
git commit -m "docs: bump version to 1.3.0 and document timed mode in readme"
```

---

## Task 6: Build, install locally, and manual test

**Files:** None (testing)

- [ ] **Step 1: Build the project in Release mode**

```powershell
dotnet publish Flow.Launcher.Plugin.Caffeine.csproj -r win-x64 -c Release
```

Expected: Publish succeeds. Output goes to `bin/Release/win-x64/publish/` (or `bin/Release/publish/` — check the output for the exact path).

- [ ] **Step 2: Find the publish output**

```powershell
Get-ChildItem bin/Release/ -Recurse -Filter "Flow.Launcher.Plugin.Caffeine.dll" | Select-Object FullName
```

Note the directory containing the DLL — that's your publish output.

- [ ] **Step 3: Close Flow Launcher**

Flow Launcher locks plugin DLLs while running. Close it before replacing files:

```powershell
Stop-Process -Name "Flow.Launcher" -Force -ErrorAction SilentlyContinue
```

- [ ] **Step 4: Back up the existing plugin and install the new build**

```powershell
# Back up existing
$pluginsDir = "$env:APPDATA\FlowLauncher\Plugins"
Rename-Item "$pluginsDir\Caffeine-1.2.0" "$pluginsDir\Caffeine-1.2.0.bak"

# Create new plugin folder and copy build output
$newDir = "$pluginsDir\Caffeine-1.3.0"
New-Item -ItemType Directory -Path $newDir -Force
Copy-Item "bin\Release\win-x64\publish\*" $newDir -Recurse
```

(Adjust the source path if the publish output is in a different location based on Step 2.)

- [ ] **Step 5: Restart Flow Launcher**

```powershell
Start-Process "$env:LOCALAPPDATA\FlowLauncher\Flow.Launcher.exe"
```

Wait a few seconds for it to fully load.

- [ ] **Step 6: Run through the testing checklist**

Test each item manually. Type the commands into Flow Launcher and observe the results:

1. Type `caf` → should show 6 preset options (30m, 1h, 2h, 5h, 8h, indefinitely)
2. Click "Keep awake for 30 minutes" → caffeine activates, tray icon appears, notification fires
3. Type `caf` again → should show status row at top ("Caffeine is active — 29m remaining") plus all presets below
4. Click "Keep awake for 2 hours" → timer restarts (no second notification, just updates)
5. Type `caf off` → should show "Turn off Caffeine" with remaining time in subtitle
6. Click it → caffeine stops, tray icon disappears, notification fires
7. Type `caf 3` → should show "Keep awake for 3 hours"
8. Type `caf 45m` → should show "Keep awake for 45 minutes"
9. Type `caf 1.5` → should show "Keep awake for 1h 30m"
10. Type `caf abc` → should show "Invalid duration" with help text
11. Type `caf 0` → should show "Invalid duration"
12. Type `caf -5` → should show "Invalid duration"
13. Right-click the tray icon (when active) → context menu should show status, duration presets, and Turn Off
14. Click a duration in the tray menu → timer should restart
15. Click "Turn Off" in the tray menu → caffeine stops
16. Hover over tray icon → tooltip should show remaining time
17. Start caffeine with a short duration (e.g. `caf 1m` for 1 minute) and wait for expiry → should auto-deactivate with "timer expired" notification, tray icon disappears

- [ ] **Step 7: Restore backup if needed**

If something goes wrong, restore the original plugin:

```powershell
$pluginsDir = "$env:APPDATA\FlowLauncher\Plugins"
Remove-Item "$pluginsDir\Caffeine-1.3.0" -Recurse -Force
Rename-Item "$pluginsDir\Caffeine-1.2.0.bak" "$pluginsDir\Caffeine-1.2.0"
```

Then restart Flow Launcher.

---

## Task 7: Open GitHub issue, push, and create PR

**Files:** None (GitHub operations)

- [ ] **Step 1: Open an issue proposing the feature**

```powershell
gh issue create --repo o850cHQk/Flow.Launcher.Plugin.Caffeine --title "Feature: Timed activation (keep awake for X hours)" --body @'
Hi! I use this plugin daily and would love to have timed activation — the ability to keep the PC awake for a specific duration (like 30 minutes, 2 hours, etc.) instead of only indefinitely.

PowerToys Awake has this feature via its tray icon, but having it directly in Flow Launcher would be much more convenient.

**Proposed behavior:**
- `caf` shows preset durations (30m, 1h, 2h, 5h, 8h, indefinite)
- `caf 3` = keep awake for 3 hours
- `caf 45m` = keep awake for 45 minutes
- `caf off` = turn off
- Right-click tray icon for quick duration access
- Timer expiry auto-deactivates with a notification

I have a working implementation ready if you're interested — happy to submit a PR.
'@
```

Note the issue number from the output (e.g. `#5`).

- [ ] **Step 2: Push the feature branch**

```powershell
git push -u origin feature/timed-mode
```

- [ ] **Step 3: Create the pull request**

Replace `#5` below with the actual issue number from Step 1:

```powershell
gh pr create --repo o850cHQk/Flow.Launcher.Plugin.Caffeine --title "feat: add timed activation mode" --body @'
## Summary

Adds duration-based activation to the Caffeine plugin, bringing feature parity with PowerToys Awake's timed mode.

- **Query presets:** `caf` shows 30m, 1h, 2h, 5h, 8h, indefinite
- **Custom durations:** `caf 3` (hours), `caf 45m` (minutes), decimals work (`caf 1.5`)
- **Turn off:** `caf off`
- **Tray context menu:** Right-click for duration presets, status, and turn off
- **Tooltip:** Shows remaining time, refreshes every 60 seconds
- **Timer expiry:** Auto-deactivates with notification
- **Backward compatible:** Existing toggle behavior unchanged (indefinite by default)

## Changes

- **New:** `CaffeineTimer.cs` — self-contained countdown clock (start, restart, remaining time, expiry event)
- **Modified:** `Main.cs` — query parsing, timer integration, six state transitions
- **Modified:** `Tray/TrayIconFactory.cs` — populated context menu with presets
- **Modified:** `Tray/TrayIconManager.cs` — tooltip refresh timer, status update plumbing
- **Modified:** `plugin.json` — version bump to 1.3.0
- **Modified:** `Readme.md` — documented timed mode commands

No changes to PowerUtilities, NativeMethods, Settings, or the settings UI.

Closes #5

## Test plan

- [ ] `caf` shows preset list when inactive
- [ ] `caf` shows status + presets when active
- [ ] `caf 3` activates for 3 hours
- [ ] `caf 45m` activates for 45 minutes
- [ ] `caf off` deactivates
- [ ] Switching duration while active restarts timer only (no PowerUtilities restart)
- [ ] Timer expiry auto-deactivates with notification
- [ ] Tray right-click menu works (duration selection and turn off)
- [ ] Tray tooltip shows remaining time
- [ ] Invalid input shows friendly error
- [ ] Auto-start still uses indefinite mode
- [ ] Plugin dispose cleans up timer and tray icon
'@
```

- [ ] **Step 4: Verify the PR was created**

```powershell
gh pr list --repo o850cHQk/Flow.Launcher.Plugin.Caffeine --author @me
```

Expected: Your PR appears in the list.
