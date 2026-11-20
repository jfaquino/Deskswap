using Deskswap.Core;

namespace Deskswap.App;

/// <summary>Runs Deskswap as a system-tray app with no main window.</summary>
internal sealed class TrayApplicationContext : ApplicationContext
{
    private readonly NotifyIcon _trayIcon;

    public TrayApplicationContext()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add(new ToolStripMenuItem($"{ProductInfo.Name} {Application.ProductVersion}") { Enabled = false });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", image: null, (_, _) => ExitThread());

        _trayIcon = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = $"{ProductInfo.Name} — {ProductInfo.Tagline}",
            ContextMenuStrip = menu,
            Visible = true,
        };
    }

    protected override void ExitThreadCore()
    {
        _trayIcon.Visible = false;
        _trayIcon.ContextMenuStrip?.Dispose();
        _trayIcon.Dispose();
        base.ExitThreadCore();
    }
}
