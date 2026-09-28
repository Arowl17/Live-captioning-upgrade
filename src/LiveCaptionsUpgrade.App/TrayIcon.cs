using System;
using System.Drawing;
using System.Windows.Forms;
using LiveCaptionsUpgrade.Core;

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
    private readonly ToolStripMenuItem _sharingOffItem;
    private readonly ToolStripMenuItem _sharingSendItem;
    private readonly ToolStripMenuItem _sharingReceiveItem;
    private readonly ToolStripMenuItem _sharingStatusItem;
    private readonly ToolStripMenuItem _pairItem;

    public TrayIcon(App app)
    {
        // Menu actions run after the menu has closed: Exit disposes this menu, and Settings opens a window.
        EventHandler Later(Action action) => (_, _) => app.Dispatcher.InvokeAsync(action);

        _copyItem = new ToolStripMenuItem("Copy", null, Later(app.CopyCaptionSelection)) { ShortcutKeyDisplayString = "Ctrl+C" };
        _showCaptionsItem = new ToolStripMenuItem("Show captions", null, Later(app.ToggleOverlayVisible));
        _clickThroughItem = new ToolStripMenuItem("Lock overlay (clicks pass through)", null, Later(app.ToggleClickThrough));
        _showLiveCaptionsItem = new ToolStripMenuItem("Show original Live Captions window", null, Later(app.ToggleLiveCaptionsWindow));

        _sharingOffItem = new ToolStripMenuItem("Off", null, Later(() => app.SetCaptionSharing(CaptionSharingMode.Off)));
        _sharingSendItem = new ToolStripMenuItem("Send captions to another computer", null, Later(() => app.SetCaptionSharing(CaptionSharingMode.Send)));
        _sharingReceiveItem = new ToolStripMenuItem("Show captions from another computer", null, Later(() => app.SetCaptionSharing(CaptionSharingMode.Receive)));
        _sharingStatusItem = new ToolStripMenuItem { Enabled = false };
        _pairItem = new ToolStripMenuItem("Pair with a computer…", null, Later(app.OpenPairing));
        var sharingMenu = new ToolStripMenuItem("Caption sharing");
        sharingMenu.DropDownItems.Add(_sharingOffItem);
        sharingMenu.DropDownItems.Add(_sharingSendItem);
        sharingMenu.DropDownItems.Add(_sharingReceiveItem);
        sharingMenu.DropDownItems.Add(new ToolStripSeparator());
        sharingMenu.DropDownItems.Add(_sharingStatusItem);
        sharingMenu.DropDownItems.Add(_pairItem);

        var menu = new ContextMenuStrip();
        menu.Items.Add(_copyItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_showCaptionsItem);
        menu.Items.Add(_clickThroughItem);
        menu.Items.Add(_showLiveCaptionsItem);
        menu.Items.Add(sharingMenu);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Settings…", null, Later(app.OpenSettings));
        menu.Items.Add("Open transcripts folder", null, Later(app.OpenTranscriptsFolder));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, Later(app.ExitApp));

        // Copy copies the text selected in the caption bar, so it's only available when some is.
        menu.Opening += (_, _) =>
        {
            _copyItem.Enabled = app.HasCaptionSelection;
            _sharingStatusItem.Text = app.SharingStatus;
        };

        _icon = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "Live Captions Upgrade",
            ContextMenuStrip = menu,
            Visible = true,
        };
    }

    public void Refresh(bool captionsVisible, string hotkey, bool clickThrough, bool liveCaptionsVisible, CaptionSharingMode sharing, string sharingStatus)
    {
        _showCaptionsItem.Checked = captionsVisible;
        _showCaptionsItem.ShortcutKeyDisplayString = hotkey.Length > 0 ? hotkey : null;
        _clickThroughItem.Checked = clickThrough;
        _showLiveCaptionsItem.Checked = liveCaptionsVisible && sharing != CaptionSharingMode.Receive;

        // Captions shown from another computer don't involve Live Captions on this one.
        _showLiveCaptionsItem.Enabled = sharing != CaptionSharingMode.Receive;
        _sharingOffItem.Checked = sharing == CaptionSharingMode.Off;
        _sharingSendItem.Checked = sharing == CaptionSharingMode.Send;
        _sharingReceiveItem.Checked = sharing == CaptionSharingMode.Receive;
        _sharingStatusItem.Text = sharingStatus;
        _pairItem.Enabled = sharing != CaptionSharingMode.Off;

        // Hovering the icon shows what's going on; tooltips are limited to 127 characters.
        string tooltip = sharing == CaptionSharingMode.Off ? "Live Captions Upgrade" : "Live Captions Upgrade – " + sharingStatus;
        _icon.Text = tooltip.Length > 127 ? tooltip[..126] + "…" : tooltip;
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
