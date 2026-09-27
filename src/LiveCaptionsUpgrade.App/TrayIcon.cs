using System;
using System.Drawing;
using System.Windows.Forms;

namespace LiveCaptionsUpgrade;

/// <summary>
/// Notification-area icon and the options menu. The same menu opens when the overlay is
/// right-clicked; the tray icon is how to reach it once the overlay is hidden or click-through.
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly ToolStripMenuItem _copyItem;
    private readonly ToolStripMenuItem _showCaptionsItem;
    private readonly ToolStripMenuItem _clickThroughItem;
    private readonly ToolStripMenuItem _showLiveCaptionsItem;

    public TrayIcon(App app)
    {
        // Menu actions run after the menu has closed: Exit disposes this menu, and Settings opens a window.
        EventHandler Later(Action action) => (_, _) => app.Dispatcher.InvokeAsync(action);

        _copyItem = new ToolStripMenuItem("Copy", null, Later(app.CopyCaptionSelection)) { ShortcutKeyDisplayString = "Ctrl+C" };
        _showCaptionsItem = new ToolStripMenuItem("Show captions", null, Later(app.ToggleOverlayVisible));
        _clickThroughItem = new ToolStripMenuItem("Lock overlay (clicks pass through)", null, Later(app.ToggleClickThrough));
        _showLiveCaptionsItem = new ToolStripMenuItem("Show original Live Captions window", null, Later(app.ToggleLiveCaptionsWindow));

        var menu = new ContextMenuStrip();
        menu.Items.Add(_copyItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_showCaptionsItem);
        menu.Items.Add(_clickThroughItem);
        menu.Items.Add(_showLiveCaptionsItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Settings…", null, Later(app.OpenSettings));
        menu.Items.Add("Open transcripts folder", null, Later(app.OpenTranscriptsFolder));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, Later(app.ExitApp));

        // Copy copies the text selected in the caption bar, so it's only available when some is.
        menu.Opening += (_, _) => _copyItem.Enabled = app.HasCaptionSelection;

        _icon = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "Live Captions Upgrade",
            ContextMenuStrip = menu,
            Visible = true,
        };
    }

    public void Refresh(bool captionsVisible, string hotkey, bool clickThrough, bool liveCaptionsVisible)
    {
        _showCaptionsItem.Checked = captionsVisible;
        _showCaptionsItem.ShortcutKeyDisplayString = hotkey.Length > 0 ? hotkey : null;
        _clickThroughItem.Checked = clickThrough;
        _showLiveCaptionsItem.Checked = liveCaptionsVisible;
    }

    public void ShowMenuAtCursor() => _icon.ContextMenuStrip?.Show(Cursor.Position);

    public void ShowNotice(string message) =>
        _icon.ShowBalloonTip(15000, "Live Captions Upgrade", message.Length > 250 ? message[..249] + "…" : message, ToolTipIcon.Info);

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.ContextMenuStrip?.Dispose();
        _icon.Dispose();
    }
}
