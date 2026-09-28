using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;

namespace LiveCaptionsUpgrade.Core.Sharing;

/// <summary>Another computer with caption sharing turned on, found on the local network.</summary>
public sealed record DiscoveredDevice(string Id, string Name, CaptionSharingMode Mode, IPAddress Address, int TcpPort, DateTime LastSeenUtc);

/// <summary>
/// Finds other computers running Live Captions Upgrade with caption sharing on, using small UDP
/// broadcasts, so nobody has to type IP addresses.
/// </summary>
public sealed class DiscoveryService : IDisposable
{
    public const int DefaultPort = 47821;
    private const string AppTag = "live-captions-upgrade";
    private const int MaxDevices = 64;
    private static readonly TimeSpan AnnounceInterval = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan ExpireAfter = TimeSpan.FromSeconds(12);

    private readonly string _deviceId;
    private readonly Func<string> _deviceName;
    private readonly CaptionSharingMode _mode;
    private readonly int _tcpPort;
    private readonly int _discoveryPort;
    private readonly ConcurrentDictionary<string, DiscoveredDevice> _devices = new();
    private readonly CancellationTokenSource _cts = new();
    private Socket? _socket;

    public DiscoveryService(string deviceId, Func<string> deviceName, CaptionSharingMode mode, int tcpPort, int discoveryPort = DefaultPort)
    {
        _deviceId = deviceId;
        _deviceName = deviceName;
        _mode = mode;
        _tcpPort = tcpPort;
        _discoveryPort = discoveryPort;
    }

    public event Action<DiscoveredDevice>? DeviceSeen;

    /// <summary>Computers heard from recently, by name.</summary>
    public IReadOnlyList<DiscoveredDevice> Devices
    {
        get
        {
            var cutoff = DateTime.UtcNow - ExpireAfter;
            return _devices.Values.Where(d => d.LastSeenUtc >= cutoff).OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }
    }

    public void Start()
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp) { EnableBroadcast = true };
        try
        {
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            if (OperatingSystem.IsWindows())
            {
                // Otherwise an ICMP "port unreachable" for an earlier broadcast makes the next receive fail.
                const int SIO_UDP_CONNRESET = unchecked((int)0x9800000C);
                socket.IOControl(SIO_UDP_CONNRESET, new byte[4], null);
            }

            socket.Bind(new IPEndPoint(IPAddress.Any, _discoveryPort));
        }
        catch
        {
            socket.Dispose();
            throw;
        }

        _socket = socket;
        _ = Task.Run(() => ReceiveLoopAsync(_cts.Token));
        _ = Task.Run(() => AnnounceLoopAsync(_cts.Token));
    }

    /// <summary>Broadcasts right away and asks everyone to answer (used when the pairing window opens).</summary>
    public void AnnounceNow() => Broadcast(query: true);

    private async Task AnnounceLoopAsync(CancellationToken token)
    {
        Broadcast(query: true);
        try
        {
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(AnnounceInterval, token).ConfigureAwait(false);
                Broadcast(query: false);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void Broadcast(bool query)
    {
        if (_socket is not { } socket)
        {
            return;
        }

        byte[] payload = Encode(query);
        foreach (var target in BroadcastAddresses())
        {
            try
            {
                socket.SendTo(payload, new IPEndPoint(target, _discoveryPort));
            }
            catch (Exception e) when (e is SocketException or ObjectDisposedException)
            {
            }
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken token)
    {
        byte[] buffer = new byte[1024];
        while (!token.IsCancellationRequested && _socket is { } socket)
        {
            SocketReceiveFromResult result;
            try
            {
                result = await socket.ReceiveFromAsync(buffer, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (SocketException)
            {
                continue;
            }

            if (!TryDecode(buffer.AsSpan(0, result.ReceivedBytes), out var beacon) || beacon.Id == _deviceId)
            {
                continue;
            }

            var from = (IPEndPoint)result.RemoteEndPoint;
            if (!_devices.ContainsKey(beacon.Id) && _devices.Count >= MaxDevices)
            {
                PruneExpired();
                if (_devices.Count >= MaxDevices)
                {
                    continue; // someone is flooding the network with fake beacons
                }
            }

            var device = new DiscoveredDevice(beacon.Id, beacon.Name, beacon.Mode, from.Address, beacon.Port, DateTime.UtcNow);
            _devices[beacon.Id] = device;
            DeviceSeen?.Invoke(device);

            if (beacon.Query)
            {
                try
                {
                    await socket.SendToAsync(Encode(query: false), SocketFlags.None, new IPEndPoint(from.Address, _discoveryPort), token).ConfigureAwait(false);
                }
                catch (Exception e) when (e is SocketException or OperationCanceledException or ObjectDisposedException)
                {
                }
            }
        }
    }

    private void PruneExpired()
    {
        var cutoff = DateTime.UtcNow - ExpireAfter;
        foreach (var (id, device) in _devices)
        {
            if (device.LastSeenUtc < cutoff)
            {
                _devices.TryRemove(id, out _);
            }
        }
    }

    private byte[] Encode(bool query) => EncodeBeacon(new Beacon(AppTag, 1, _deviceId, _deviceName(), _mode, _tcpPort, query));

    internal static byte[] EncodeBeacon(Beacon beacon) => JsonSerializer.SerializeToUtf8Bytes(beacon, ControlJson.Options);

    internal static bool TryDecode(ReadOnlySpan<byte> data, out Beacon beacon)
    {
        beacon = default!;
        try
        {
            var decoded = JsonSerializer.Deserialize<Beacon>(data, ControlJson.Options);
            if (decoded is null || decoded.App != AppTag || !IsValidId(decoded.Id) || !PairingSession.IsValidPort(decoded.Port)
                || !Enum.IsDefined(decoded.Mode))
            {
                return false;
            }

            beacon = decoded with { Name = DeviceNames.Sanitize(decoded.Name) };
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>The general broadcast address plus each network's own (some routers only pass the latter).</summary>
    internal static IEnumerable<IPAddress> BroadcastAddresses()
    {
        var addresses = new HashSet<IPAddress> { IPAddress.Broadcast };
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                {
                    continue;
                }

                foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
                {
                    if (unicast.Address.AddressFamily != AddressFamily.InterNetwork || unicast.IPv4Mask is null)
                    {
                        continue;
                    }

                    byte[] ip = unicast.Address.GetAddressBytes();
                    byte[] mask = unicast.IPv4Mask.GetAddressBytes();
                    byte[] broadcast = new byte[4];
                    for (int i = 0; i < 4; i++)
                    {
                        broadcast[i] = (byte)(ip[i] | ~mask[i]);
                    }

                    addresses.Add(new IPAddress(broadcast));
                }
            }
        }
        catch (NetworkInformationException)
        {
        }

        return addresses;
    }

    /// <summary>This computer's IPv4 addresses on its networks, to show so they can be typed on the other computer.</summary>
    public static IReadOnlyList<IPAddress> LocalAddresses()
    {
        var result = new List<IPAddress>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                {
                    continue;
                }

                var properties = nic.GetIPProperties();

                // Adapters with a gateway are the real networks; VPN and virtual adapters usually have none.
                bool hasGateway = properties.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any));
                foreach (var unicast in properties.UnicastAddresses)
                {
                    var address = unicast.Address;
                    if (address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(address) && !IsLinkLocal(address))
                    {
                        if (hasGateway)
                        {
                            result.Insert(0, address);
                        }
                        else
                        {
                            result.Add(address);
                        }
                    }
                }
            }
        }
        catch (NetworkInformationException)
        {
        }

        return result.Distinct().ToList();
    }

    private static bool IsLinkLocal(IPAddress address)
    {
        byte[] bytes = address.GetAddressBytes();
        return bytes[0] == 169 && bytes[1] == 254;
    }

    public void Dispose()
    {
        _cts.Cancel();
        _socket?.Dispose();
    }

    /// <summary>Device ids are SHA-256 fingerprints: 64 lowercase hex characters.</summary>
    internal static bool IsValidId(string? id) =>
        id is { Length: 64 } && id.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    internal sealed record Beacon(string App, int V, string Id, string Name, CaptionSharingMode Mode, int Port, bool Query);
}
