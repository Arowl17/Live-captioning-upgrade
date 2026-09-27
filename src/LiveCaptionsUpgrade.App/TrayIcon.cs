using System;
using System.Drawing;
using System.Windows.Forms;

namespace LiveCaptionsUpgrade;

/// <summary>
/// Notification-area icon and the options menu. The same menu opens when the overlay is
/// right-clicked; the tray icon is how to reach it once the overlay is click-through.
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly ToolStripMenuItem _clickThroughItem;
    private readonly ToolStripMenuItem _showLiveCaptionsItem;

    public TrayIcon(App app)
    {
        _clickThroughItem = new ToolStripMenuItem("Lock overlay (clicks pass through)", null, (_, _) => app.ToggleClickThrough());
        _showLiveCaptionsItem = new ToolStripMenuItem("Show original Live Captions window", null, (_, _) => app.ToggleLiveCaptionsWindow());

        var menu = new ContextMenuStrip();
        menu.Items.Add(_clickThroughItem);
        menu.Items.Add(_showLiveCaptionsItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Open transcripts folder", null, (_, _) => app.OpenTranscriptsFolder());
        menu.Items.Add("Edit settings (restart to apply)", null, (_, _) => app.OpenSettingsFile());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => app.ExitApp());

        _icon = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "Live Captions Upgrade",
            ContextMenuStrip = menu,
            Visible = true,
        };
    }

    public void Refresh(bool clickThrough, bool liveCaptionsVisible)
    {
        _clickThroughItem.Checked = clickThrough;
        _showLiveCaptionsItem.Checked = liveCaptionsVisible;
    }

    public void ShowMenuAtCursor() => _icon.ContextMenuStrip?.Show(Cursor.Position);

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.ContextMenuStrip?.Dispose();
        _icon.Dispose();
    }
}
