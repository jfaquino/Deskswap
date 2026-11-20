using Deskswap.Platform;

namespace Deskswap.App;

/// <summary>
/// The desktop indicator (design §6.2): a small label centered on one monitor for about a second. It never takes
/// focus, ignores the mouse and stays above other windows.
/// </summary>
internal sealed class IndicatorForm : Form
{
    private const int WsExTopmost = 0x0000_0008;
    private const int WsExTransparent = 0x0000_0020;
    private const int WsExToolWindow = 0x0000_0080;
    private const int WsExNoActivate = 0x0800_0000;

    private static readonly Color Background = Color.FromArgb(32, 32, 32);
    private static readonly Color Subtle = Color.FromArgb(200, 200, 200);

    private readonly System.Windows.Forms.Timer _hideTimer = new() { Interval = 1100 };
    private readonly Font _titleFont = new("Segoe UI Semibold", 20f);
    private readonly Font _subtitleFont = new("Segoe UI", 11f);
    private string _title = string.Empty;
    private string? _subtitle;

    public IndicatorForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        BackColor = Background;
        ForeColor = Color.White;
        Opacity = 0.92;
        DoubleBuffered = true;
        _hideTimer.Tick += (_, _) =>
        {
            _hideTimer.Stop();
            Hide();
        };
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var parameters = base.CreateParams;
            parameters.ExStyle |= WsExTopmost | WsExTransparent | WsExToolWindow | WsExNoActivate;
            return parameters;
        }
    }

    /// <summary>Shows the indicator centered on a monitor and restarts the fade-out timer.</summary>
    /// <param name="title">The main line, e.g. "Desktop 3" or the desktop's name.</param>
    /// <param name="subtitle">An optional second line.</param>
    /// <param name="monitorBounds">The monitor's bounds in physical pixels.</param>
    public void Present(string title, string? subtitle, Rectangle monitorBounds)
    {
        _title = title;
        _subtitle = subtitle;

        float scale = DeviceDpi / 96f;
        int padding = (int)(20 * scale);
        var titleSize = TextRenderer.MeasureText(_title, _titleFont);
        var subtitleSize = _subtitle is null ? Size.Empty : TextRenderer.MeasureText(_subtitle, _subtitleFont);
        int width = Math.Max(Math.Max(titleSize.Width, subtitleSize.Width) + (2 * padding), (int)(180 * scale));
        int height = titleSize.Height + subtitleSize.Height + (2 * padding);

        Bounds = new Rectangle(
            monitorBounds.Left + ((monitorBounds.Width - width) / 2),
            monitorBounds.Top + ((monitorBounds.Height - height) / 2),
            width,
            height);

        if (!Visible)
        {
            Show();
            WindowStyling.UseRoundedCorners(Handle);
        }

        Invalidate();
        _hideTimer.Stop();
        _hideTimer.Start();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var titleSize = TextRenderer.MeasureText(_title, _titleFont);
        var subtitleSize = _subtitle is null ? Size.Empty : TextRenderer.MeasureText(_subtitle, _subtitleFont);
        int top = (ClientSize.Height - titleSize.Height - subtitleSize.Height) / 2;

        TextRenderer.DrawText(e.Graphics, _title, _titleFont, new Rectangle(0, top, ClientSize.Width, titleSize.Height), ForeColor, TextFormatFlags.HorizontalCenter);
        if (_subtitle is not null)
        {
            TextRenderer.DrawText(e.Graphics, _subtitle, _subtitleFont, new Rectangle(0, top + titleSize.Height, ClientSize.Width, subtitleSize.Height), Subtle, TextFormatFlags.HorizontalCenter);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _hideTimer.Dispose();
            _titleFont.Dispose();
            _subtitleFont.Dispose();
        }

        base.Dispose(disposing);
    }
}
