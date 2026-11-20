using System.ComponentModel;
using System.Diagnostics;
using Deskswap.App.Resources;
using Deskswap.Core;
using Deskswap.Core.Input;
using Deskswap.Platform;
using Microsoft.Win32;

namespace Deskswap.App;

/// <summary>
/// Runs Deskswap as a system-tray app: wires the keyboard hook, timers and system events to the
/// <see cref="DeskswapController"/>, and offers Show all windows / Resume / Exit in the tray menu.
/// </summary>
internal sealed class TrayApplicationContext : ApplicationContext
{
    /// <summary>The safety-net sweep interval. M2 replaces most of this with window events (design §12).</summary>
    private const int ReconcileIntervalMs = 1000;

    /// <summary>The layout is saved at most this often, and only when it changed (design §10).</summary>
    private const int SaveIntervalMs = 2000;

    private readonly Control _dispatcher = new();
    private readonly DeskswapController _controller;
    private readonly NotifyIcon _trayIcon;
    private readonly Icon _icon;
    private readonly ToolStripMenuItem _showAllItem;
    private readonly ToolStripMenuItem _resumeItem;
    private readonly KeyboardHook _keyboardHook;
    private readonly System.Windows.Forms.Timer _reconcileTimer = new() { Interval = ReconcileIntervalMs };
    private readonly System.Windows.Forms.Timer _saveTimer = new() { Interval = SaveIntervalMs };
    private readonly System.Windows.Forms.Timer _displayChangeTimer = new() { Interval = 500 };
    private Process? _watchdog;

    public TrayApplicationContext()
    {
        _ = _dispatcher.Handle; // Create the handle now so the hook thread can post to the UI thread.

        _showAllItem = new ToolStripMenuItem(Strings.MenuShowAllAndPause, null, (_, _) => _controller!.Pause());
        _resumeItem = new ToolStripMenuItem(Strings.MenuResume, null, (_, _) => _controller!.Resume()) { Visible = false };
        var menu = new ContextMenuStrip();
        menu.Items.Add(new ToolStripMenuItem($"{ProductInfo.Name} {Application.ProductVersion.Split('+')[0]}") { Enabled = false });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_showAllItem);
        menu.Items.Add(_resumeItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(Strings.MenuExit, null, (_, _) => ExitThread());

        _icon = TrayIconArt.Create();
        _trayIcon = new NotifyIcon
        {
            Icon = _icon,
            Text = Truncate(Strings.AppTooltip),
            ContextMenuStrip = menu,
            Visible = true,
        };

        _controller = new DeskswapController();
        _controller.Notify += (_, message) => _trayIcon.ShowBalloonTip(5000, ProductInfo.Name, message, ToolTipIcon.Info);
        _controller.PausedChanged += (_, _) => OnPausedChanged();
        _controller.Initialize();

        _keyboardHook = new KeyboardHook(ShortcutMap.Defaults, action => _dispatcher.BeginInvoke(() => _controller.Execute(action)));
        try
        {
            _keyboardHook.Start();
        }
        catch (Win32Exception ex)
        {
            Log.Error("Keyboard hook failed to start.", ex);
            _trayIcon.ShowBalloonTip(5000, ProductInfo.Name, Strings.NotifyHookFailed, ToolTipIcon.Warning);
        }

        StartWatchdog();

        _reconcileTimer.Tick += (_, _) => _controller.Tick();
        _saveTimer.Tick += (_, _) => _controller.SaveLayout();
        _displayChangeTimer.Tick += (_, _) =>
        {
            _displayChangeTimer.Stop();
            _controller.OnDisplaysChanged();
        };
        _reconcileTimer.Start();
        _saveTimer.Start();

        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        SystemEvents.SessionSwitch += OnSessionSwitch;
        OnPausedChanged();
    }

    /// <summary>Shows every window if the UI thread hits an unhandled exception.</summary>
    /// <param name="exception">The exception.</param>
    public void OnUnhandledException(Exception exception)
    {
        Log.Error("Unhandled exception.", exception);
        _controller.Pause();
    }

    protected override void ExitThreadCore()
    {
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        _reconcileTimer.Stop();
        _saveTimer.Stop();
        _displayChangeTimer.Stop();
        _keyboardHook.Dispose();
        _controller.Shutdown();
        _controller.Dispose();
        _watchdog?.Dispose();

        _trayIcon.Visible = false;
        _trayIcon.ContextMenuStrip?.Dispose();
        _trayIcon.Dispose();
        _icon.Dispose();
        _reconcileTimer.Dispose();
        _saveTimer.Dispose();
        _displayChangeTimer.Dispose();
        _dispatcher.Dispose();
        base.ExitThreadCore();
    }

    private static string Truncate(string text) => text.Length <= 63 ? text : text[..62] + "…";

    private void StartWatchdog()
    {
        if (Environment.ProcessPath is not { } executable)
        {
            return;
        }

        try
        {
            _watchdog = Watchdog.Start(executable, AppPaths.Journal);
        }
        catch (Win32Exception ex)
        {
            Log.Error("Could not start the watchdog.", ex);
        }
    }

    private void OnPausedChanged()
    {
        bool paused = _controller.IsPaused;

        // While paused, only the pause toggle stays active so it can resume; every other key passes through.
        _keyboardHook.SetMap(paused ? ShortcutMap.Defaults.Only(ShortcutCommand.TogglePause) : ShortcutMap.Defaults);
        _showAllItem.Visible = !paused;
        _resumeItem.Visible = paused;
        _trayIcon.Text = Truncate(paused ? Strings.AppTooltipPaused : Strings.AppTooltip);
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e) =>
        _dispatcher.BeginInvoke(() =>
        {
            // Displays often change in several steps; act once things settle.
            _displayChangeTimer.Stop();
            _displayChangeTimer.Start();
        });

    private void OnSessionSwitch(object? sender, SessionSwitchEventArgs e)
    {
        if (e.Reason is SessionSwitchReason.SessionUnlock or SessionSwitchReason.SessionLogon or SessionSwitchReason.ConsoleConnect)
        {
            // Key releases are missed while the session is locked.
            _keyboardHook.ResetKeyState();
        }
    }
}

/// <summary>Draws the tray icon: two screens side by side.</summary>
internal static class TrayIconArt
{
    public static Icon Create()
    {
        using var bitmap = new Bitmap(32, 32);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var left = new SolidBrush(Color.FromArgb(0, 120, 212));
            using var right = new SolidBrush(Color.FromArgb(96, 205, 255));
            graphics.FillRectangle(left, 1, 7, 14, 18);
            graphics.FillRectangle(right, 17, 7, 14, 18);
        }

        nint handle = bitmap.GetHicon();
        using var temporary = Icon.FromHandle(handle);
        var icon = (Icon)temporary.Clone();
        NativeIcons.Destroy(handle);
        return icon;
    }
}
