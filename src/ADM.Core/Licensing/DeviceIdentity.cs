using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace Adm.Core.Licensing;

/// <summary>
/// A stable identifier for this machine. It is derived from the Windows
/// MachineGuid and the computer name, then hashed, so the raw identifier is
/// never sent to the license server.
/// </summary>
public static class DeviceIdentity
{
    private static readonly Lazy<string> Cached = new(Compute, isThreadSafe: true);

    /// <summary>The device id used when activating and verifying a license.</summary>
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
        string material = $"{ReadMachineGuid()}|{ReadMachineName()}";
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(material));
        return Convert.ToHexString(hash)[..32].ToLowerInvariant();
    }

    private static string ReadMachineGuid()
    {
        if (!OperatingSystem.IsWindows())
            return string.Empty;

        try
        {
            using var key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var sub = key.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
            if (sub?.GetValue("MachineGuid") is string guid && guid.Length > 0)
                return guid;
        }
        catch (Exception)
        {
            // The registry may not be readable; the fallbacks below still work.
        }

        return string.Empty;
    }

    private static string ReadMachineName()
    {
        try
        {
            return Environment.MachineName;
        }
        catch (Exception)
        {
            return "unknown";
        }
    }
}
