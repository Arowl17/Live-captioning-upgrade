using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using LiveCaptionsUpgrade.Core;
using LiveCaptionsUpgrade.Core.Sharing;

namespace LiveCaptionsUpgrade;

internal static class AppPaths
{
    /// <summary>%APPDATA%\LiveCaptionsUpgrade: settings, pairing and this computer's identity.</summary>
    public static string DataDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LiveCaptionsUpgrade");

    /// <summary>%LOCALAPPDATA%\LiveCaptionsUpgrade\logs.</summary>
    public static string LogDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LiveCaptionsUpgrade", "logs");

    public static string IdentityFile => Path.Combine(DataDirectory, "identity.bin");

    public static string ExecutablePath => Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "LiveCaptionsUpgrade.exe");
}

/// <summary>Keeps this computer's certificate on disk, encrypted for the current Windows user (DPAPI).</summary>
internal static class IdentityStore
{
    public static DeviceIdentity LoadOrCreate()
    {
        string path = AppPaths.IdentityFile;
        if (File.Exists(path))
        {
            try
            {
                byte[] pkcs12 = ProtectedData.Unprotect(File.ReadAllBytes(path), null, DataProtectionScope.CurrentUser);
                return DeviceIdentity.FromPkcs12(pkcs12);
            }
            catch (Exception e) when (e is CryptographicException or IOException)
            {
                // A new identity means pairing again, but the app keeps working.
                Log.Error("Stored identity unreadable; creating a new one", e);
                File.Copy(path, path + ".bad", overwrite: true);
            }
        }

        var identity = DeviceIdentity.Create();
        Directory.CreateDirectory(AppPaths.DataDirectory);
        byte[] protectedBytes = ProtectedData.Protect(identity.ExportPkcs12(), null, DataProtectionScope.CurrentUser);
        string temporary = path + ".tmp";
        File.WriteAllBytes(temporary, protectedBytes);
        File.Move(temporary, path, overwrite: true);
        Log.Info($"Created device identity {DeviceIdentity.ShortFingerprint(identity.Fingerprint)}");
        return identity;
    }
}

/// <summary>
/// The Windows Firewall rule that lets the other computer connect to this one. Adding it takes one
/// administrator prompt; checking for it doesn't.
/// </summary>
internal static class FirewallRule
{
    public const string RuleName = "Live Captions Upgrade";

    /// <summary>True when our inbound rule exists for this copy of the program.</summary>
    public static bool Exists()
    {
        try
        {
            // netsh output is translated, but the program path appears as is. It's in the console's code page, so a
            // path with non-English letters may not come through intact; then the file name has to do.
            var (exitCode, output) = RunNetsh($"advfirewall firewall show rule name=\"{RuleName}\" dir=in verbose");
            string exe = AppPaths.ExecutablePath;
            string expected = exe.All(char.IsAscii) ? exe : Path.GetFileName(exe);
            return exitCode == 0 && output.Contains(expected, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception e)
        {
            Log.Warn($"Could not query firewall rules: {e.Message}");
            return false;
        }
    }

    /// <summary>Adds (or replaces) the rule; shows one administrator prompt. Returns false if it was declined or failed.</summary>
    public static bool Add()
    {
        string exe = AppPaths.ExecutablePath;
        string script =
            $"netsh advfirewall firewall delete rule name=\"{RuleName}\" & " +
            $"netsh advfirewall firewall add rule name=\"{RuleName}\" dir=in action=allow program=\"{exe}\" enable=yes profile=any";
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c {script}",
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            });
            if (process is null || !process.WaitForExit(60_000))
            {
                return false;
            }

            bool added = process.ExitCode == 0;
            Log.Info(added ? "Firewall rule added" : $"Adding the firewall rule failed (exit code {process.ExitCode})");
            return added;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            Log.Info("Firewall rule not added: the administrator prompt was declined");
            return false;
        }
    }

    private static (int ExitCode, string Output) RunNetsh(string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "netsh.exe",
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            CreateNoWindow = true,
        })!;
        string output = process.StandardOutput.ReadToEnd();
        process.WaitForExit(10_000);
        return (process.ExitCode, output);
    }
}

/// <summary>Writes <see cref="Log"/> messages to %LOCALAPPDATA%\LiveCaptionsUpgrade\logs\app.log.</summary>
internal static class FileLog
{
    private const long MaxSize = 1024 * 1024;
    private static readonly object Gate = new();
    private static string? _path;

    public static void Start()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.LogDirectory);
            _path = Path.Combine(AppPaths.LogDirectory, "app.log");
            Log.Written += Write;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // No log then; the app works the same.
        }
    }

    private static void Write(string level, string message)
    {
        if (_path is null)
        {
            return;
        }

        lock (Gate)
        {
            try
            {
                var file = new FileInfo(_path);
                if (file.Exists && file.Length > MaxSize)
                {
                    File.Move(_path, Path.ChangeExtension(_path, ".old.log"), overwrite: true);
                }

                string line = string.Create(CultureInfo.InvariantCulture, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {level,-5} {message}{Environment.NewLine}");
                File.AppendAllText(_path, line, Encoding.UTF8);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }
    }
}
