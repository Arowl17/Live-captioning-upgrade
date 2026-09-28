using System;
using System.ComponentModel;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using LiveCaptionsUpgrade.Core;
using LiveCaptionsUpgrade.Core.Sharing;

namespace LiveCaptionsUpgrade;

/// <summary>Finds the other computer, shows the pairing code on both, and records the pairing once both users confirm.</summary>
public partial class PairingWindow : Window
{
    private readonly SharingController _sharing;
    private readonly DispatcherTimer _refreshTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private PairingSession? _session;
    private bool _pairingFinished = true;
    private bool _busy;
    private string? _shownNearby;

    /// <summary>Opens at the first step: choosing the computer to pair with.</summary>
    internal PairingWindow(SharingController sharing)
    {
        _sharing = sharing;
        InitializeComponent();
        ShowOwnAddress();
        RefreshNearby();
        _sharing.AnnounceNow();
        _refreshTimer.Tick += (_, _) => RefreshNearby();
        _refreshTimer.Start();
    }

    /// <summary>Opens at the code step, for a request from another computer.</summary>
    internal PairingWindow(SharingController sharing, PairingSession incoming)
        : this(sharing)
    {
        Topmost = true;
        ShowCode(incoming, isIncoming: true);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_pairingFinished)
        {
            _session?.Reject();
        }

        _refreshTimer.Stop();
        base.OnClosing(e);
    }

    private void ShowOwnAddress()
    {
        var addresses = DiscoveryService.LocalAddresses();
        int? port = _sharing.ListenPort;
        string suffix = port is int p && p != FramedConnection.DefaultPort ? ":" + p : string.Empty;
        OwnAddressText.Text = addresses.Count == 0
            ? $"This computer is {_sharing.ComputerName}."
            : $"This computer is {_sharing.ComputerName}, IP address {string.Join(" or ", addresses.Select(a => a + suffix))}.";
    }

    private void RefreshNearby()
    {
        var rows = _sharing.Nearby.Select(d => new NearbyRow(d)).ToList();

        // Only rebuild the list when it changed, so a Pair button isn't replaced while it's being clicked.
        string shown = string.Join("|", rows.Select(r => r.Device.Id + r.Detail));
        if (shown != _shownNearby)
        {
            _shownNearby = shown;
            NearbyList.ItemsSource = rows;
        }

        NoNearbyText.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        NoNearbyText.Text = _sharing.IsRunning
            ? "Looking… If the other computer doesn't appear, enter its IP address below."
            : "Caption sharing is off on this computer. Turn it on first (Send or Show).";
    }

    private async void OnPairNearby(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: NearbyRow row })
        {
            await StartPairingAsync(row.Name, new IPEndPoint(row.Device.Address, row.Device.TcpPort));
        }
    }

    private void OnAddressKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            OnPairByAddress(sender, e);
        }
    }

    private async void OnPairByAddress(object sender, RoutedEventArgs e)
    {
        string text = AddressBox.Text.Trim();
        if (text.Length == 0)
        {
            AddressBox.Focus();
            return;
        }

        string host = text;
        int port = FramedConnection.DefaultPort;
        int colon = text.LastIndexOf(':');
        if (colon > 0 && text.Count(c => c == ':') == 1 && int.TryParse(text[(colon + 1)..], out int parsedPort) && parsedPort is > 0 and <= 65535)
        {
            host = text[..colon];
            port = parsedPort;
        }

        IPAddress? address;
        if (!IPAddress.TryParse(host, out address))
        {
            try
            {
                address = (await Dns.GetHostAddressesAsync(host)).FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
            }
            catch (Exception ex) when (ex is SocketException or ArgumentException)
            {
                address = null;
            }
        }

        if (address is null)
        {
            MessageBox.Show(this, $"Couldn't find \"{host}\". Check the IP address shown in the pairing window on the other computer.",
                Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        await StartPairingAsync(host, new IPEndPoint(address, port));
    }

    private async Task StartPairingAsync(string name, IPEndPoint endPoint)
    {
        if (_busy)
        {
            return;
        }

        _busy = true;
        Cursor = Cursors.Wait;
        try
        {
            var session = await _sharing.PairAsync(endPoint);
            ShowCode(session, isIncoming: false);
        }
        catch (Exception ex)
        {
            Log.Warn($"Pairing with {name} ({endPoint}) failed: {ex.Message}");
            string reason = ex switch
            {
                InvalidOperationException when !_sharing.IsRunning => "Caption sharing is off on this computer. Turn it on first (Send or Show).",
                InvalidOperationException => "Another pairing is still in progress. Finish or cancel it first.",
                _ => $"Couldn't reach {name}.\n\nMake sure Live Captions Upgrade is open there with caption sharing turned on (Send or Show), "
                    + "that both computers are on the same network, and that Windows Firewall on it allows Live Captions Upgrade.",
            };
            MessageBox.Show(this, reason, Title, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _busy = false;
            Cursor = null;
        }
    }

    private void ShowCode(PairingSession session, bool isIncoming)
    {
        _session = session;
        _pairingFinished = false;
        _refreshTimer.Stop();
        ChoosePanel.Visibility = Visibility.Collapsed;
        CodePanel.Visibility = Visibility.Visible;
        CodeTitle.Text = isIncoming
            ? $"{session.PeerName} wants to pair with this computer"
            : $"Pair with {session.PeerName}";
        CodeHint.Text = $"Check that {session.PeerName} shows exactly the same code, then click \"The codes match\" on both computers.";
        CodeText.Text = PairingCode.Format(session.Code);
        ConfirmButton.IsDefault = true;
        ConfirmButton.Focus();
        _ = WaitForResultAsync(session);
    }

    private async Task WaitForResultAsync(PairingSession session)
    {
        bool paired = await session.Result;
        _pairingFinished = true;
        Topmost = false;
        CodePanel.Visibility = Visibility.Collapsed;
        ResultPanel.Visibility = Visibility.Visible;
        ResultClose.IsDefault = true;
        if (!paired)
        {
            ResultTitle.Text = "Not paired";
            ResultText.Text = $"Pairing with {session.PeerName} was cancelled or timed out. Nothing was changed.";
            return;
        }

        ResultTitle.Text = $"Paired with {session.PeerName}";
        var mode = _sharing.Mode;
        ResultText.Text = (mode, session.PeerMode) switch
        {
            (CaptionSharingMode.Send, CaptionSharingMode.Receive) => $"Captions from this computer will now appear on {session.PeerName}.",
            (CaptionSharingMode.Receive, CaptionSharingMode.Send) => $"Captions from {session.PeerName} will now appear on this computer.",
            _ => "Now set one computer to send captions and the other to show them: right-click the captions > Caption sharing.",
        };
    }

    private void OnConfirm(object sender, RoutedEventArgs e)
    {
        _session?.Confirm();
        ConfirmButton.IsEnabled = false;
        WaitingText.Text = $"Waiting for {_session?.PeerName} to confirm…";
        WaitingText.Visibility = Visibility.Visible;
    }

    private void OnCancelPairing(object sender, RoutedEventArgs e)
    {
        _session?.Reject();
        Close();
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}

/// <summary>A computer in the pairing window's list.</summary>
internal sealed record NearbyRow(DiscoveredDevice Device)
{
    public string Name => Device.Name;

    public string Detail => Device.Mode switch
    {
        CaptionSharingMode.Send => $"Sends captions · {Device.Address}",
        CaptionSharingMode.Receive => $"Shows captions · {Device.Address}",
        _ => Device.Address.ToString(),
    };
}
