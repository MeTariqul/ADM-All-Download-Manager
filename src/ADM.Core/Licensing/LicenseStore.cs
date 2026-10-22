using System.Text.Json;
using System.Text.Json.Serialization;

namespace Adm.Core.Licensing;

/// <summary>What is stored on disk about the licence.</summary>
public sealed class LicenseRecord
{
    public string? Key { get; set; }

    /// <summary>The signed token.</summary>
    public string? Token { get; set; }

    public DateTimeOffset ActivatedAt { get; set; }

    public static LicenseRecord FromItem(string key, string token) => new()
    {
        Key = key,
        Token = token,
        ActivatedAt = DateTimeOffset.Now
    };
}

/// <summary>Stores the licence next to the other settings.</summary>
public sealed class LicenseStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly string _path;

    public LicenseStore(string? path = null)
    {
        _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ADM", "license.json");
    }

    public string FilePath => _path;

    public LicenseRecord? Load()
    {
        try
        {
            if (!File.Exists(_path))
                return null;

            string json = File.ReadAllText(_path);
            return JsonSerializer.Deserialize<LicenseRecord>(json, JsonOptions);
        }
        catch (Exception)
        {
            // A corrupt file simply means the user has to activate again.
            return null;
        }
    }

    public void Save(LicenseRecord record)
    {
        try
        {
            string? directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            string temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(record, JsonOptions));
            File.Move(temp, _path, overwrite: true);
        }
        catch (Exception)
        {
            // Losing the licence only means the user has to activate again.
        }
    }

    public void Clear()
    {
        try
        {
            if (File.Exists(_path))
                File.Delete(_path);
        }
        catch (Exception)
        {
            // Nothing else to do.
        }
    }
}
