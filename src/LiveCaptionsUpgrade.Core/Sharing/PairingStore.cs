using System.Net;
using System.Text.Json;

namespace LiveCaptionsUpgrade.Core.Sharing;

/// <summary>The computer this one is paired with.</summary>
/// <param name="Id">Its certificate fingerprint.</param>
/// <param name="Name">Its computer name.</param>
/// <param name="Address">Where it was last reached, for reconnecting when discovery can't find it.</param>
/// <param name="Port">The port it accepts connections on.</param>
public sealed record PairedComputer(string Id, string Name, string? Address, int Port);

/// <summary>
/// Remembers the one computer this one is paired with, in pairing.json next to the settings.
/// Kept apart from the settings so the settings window can't overwrite a pairing made while it's open.
/// Thread-safe.
/// </summary>
public sealed class PairingStore : ITrustStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _path;
    private readonly object _lock = new();
    private PairedComputer? _paired;

    public PairingStore(string path)
    {
        _path = path;
        _paired = Load(path);
    }

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LiveCaptionsUpgrade", "pairing.json");

    /// <summary>Raised (on any thread) when the paired computer changes or is forgotten.</summary>
    public event Action? Changed;

    public PairedComputer? Paired
    {
        get
        {
            lock (_lock)
            {
                return _paired;
            }
        }
    }

    public bool IsTrusted(string fingerprint)
    {
        lock (_lock)
        {
            return _paired is not null && string.Equals(_paired.Id, fingerprint, StringComparison.Ordinal);
        }
    }

    /// <summary>Pairs with a computer, replacing any earlier pairing.</summary>
    public void Trust(string fingerprint, string name, IPEndPoint endPoint)
    {
        lock (_lock)
        {
            _paired = new PairedComputer(fingerprint, DeviceNames.Sanitize(name), endPoint.Address.ToString(), endPoint.Port);
            Save();
        }

        Changed?.Invoke();
    }

    /// <summary>Remembers the paired computer's current name and address after connecting to it.</summary>
    public void UpdateLastSeen(string fingerprint, string name, IPEndPoint endPoint)
    {
        lock (_lock)
        {
            if (_paired is null || !string.Equals(_paired.Id, fingerprint, StringComparison.Ordinal))
            {
                return;
            }

            var updated = _paired with { Name = DeviceNames.Sanitize(name), Address = endPoint.Address.ToString(), Port = endPoint.Port };
            if (updated == _paired)
            {
                return;
            }

            _paired = updated;
            Save();
        }
    }

    public void Forget()
    {
        lock (_lock)
        {
            if (_paired is null)
            {
                return;
            }

            _paired = null;
            Save();
        }

        Changed?.Invoke();
    }

    private static PairedComputer? Load(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var loaded = JsonSerializer.Deserialize<PairedComputer>(File.ReadAllText(path), JsonOptions);
            return loaded is not null && DiscoveryService.IsValidId(loaded.Id)
                ? loaded with { Name = DeviceNames.Sanitize(loaded.Name), Port = PairingSession.IsValidPort(loaded.Port) ? loaded.Port : FramedConnection.DefaultPort }
                : null;
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            Log.Error("Pairing file unreadable", e);
            return null;
        }
    }

    private void Save()
    {
        try
        {
            if (_paired is null)
            {
                File.Delete(_path);
                return;
            }

            string? directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            string temporary = _path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(_paired, JsonOptions));
            File.Move(temporary, _path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // The pairing still works until the app closes.
            Log.Error("Saving the pairing failed", e);
        }
    }
}
