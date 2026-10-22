using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace Adm.Core.Licensing;

/// <summary>
/// A stable identifier for this machine. It is the Windows MachineGuid in the
/// canonical GUID form, which is the same value the licensing service and the
/// browser extension already use, so a licence behaves identically across
/// applications.
/// </summary>
public static class DeviceIdentity
{
    private const string DeviceIdFileName = "device.id";

    private static readonly Lazy<string> Cached = new(Compute, isThreadSafe: true);

    /// <summary>The device id used when activating and verifying a licence.</summary>
    public static string DeviceId => Cached.Value;

    /// <summary>A short form of the device id that a user can read out.</summary>
    public static string ShortId
    {
        get
        {
            string id = DeviceId;
            return id.Length <= 12 ? id : id[..12].ToUpperInvariant();
        }
    }

    private static string Compute()
    {
        string? guid = ReadMachineGuid();
        if (guid is not null)
            return guid;

        // The registry was not readable, so a generated id is kept on disk to
        // stay stable across launches.
        try
        {
            string path = DeviceIdPath;
            if (File.Exists(path))
            {
                string existing = File.ReadAllText(path).Trim();
                if (Guid.TryParse(existing, out Guid parsed))
                    return parsed.ToString("D");
            }

            Guid created = Guid.NewGuid();
            string? directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            File.WriteAllText(path, created.ToString("D"));
            return created.ToString("D");
        }
        catch (Exception)
        {
            return Environment.MachineName.ToLowerInvariant();
        }
    }

    /// <summary>Where the generated device id is kept when the registry is unreadable.</summary>
    public static string DeviceIdPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ADM", DeviceIdFileName);

    private static string? ReadMachineGuid()
    {
        if (!OperatingSystem.IsWindows())
            return null;

        try
        {
            using var key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var sub = key.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
            if (sub?.GetValue("MachineGuid") is string guid && Guid.TryParse(guid.Trim(), out Guid parsed))
                return parsed.ToString("D");
        }
        catch (Exception)
        {
            // The registry may not be readable; the generated id is used then.
        }

        return null;
    }
}
