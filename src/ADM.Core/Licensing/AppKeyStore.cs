using System;
using System.IO;

namespace Adm.Core.Licensing;

/// <summary>
/// Reads the app key from the build. The value is compiled into the
/// application, but it can be replaced at run time, so a build that was
/// packaged with an incomplete key can be repaired without a rebuild.
/// </summary>
public static class AppKeyStore
{
    private const string FileName = "appkey.txt";

    /// <summary>The file the run time override is read from and written to.</summary>
    public static string FilePath => Path.Combine(AppContext.BaseDirectory, FileName);

    /// <summary>The app key that is actually used, including any override.</summary>
    public static string Current => LicenseConfiguration.EffectiveAppKey;

    /// <summary>
    /// Stores an app key next to the executable. Returns false when the file
    /// cannot be written, for example when the app runs from a read only
    /// folder.
    /// </summary>
    public static bool TrySave(string appKey, out string? error)
    {
        error = null;

        try
        {
            string value = (appKey ?? string.Empty).Trim();
            File.WriteAllText(FilePath, value);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>Removes the run time override.</summary>
    public static bool TryClear(out string? error)
    {
        error = null;

        try
        {
            if (File.Exists(FilePath))
                File.Delete(FilePath);

            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }
}
