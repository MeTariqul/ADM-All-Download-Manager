using System.Text.Json;
using System.Text.Json.Serialization;
using Adm.Core.Models;

namespace Adm.Core.Storage;

/// <summary>Atomically persists the download list (history + resume state).</summary>
public sealed class DownloadStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _path;

    public DownloadStore(string? path = null)
    {
        _path = path ?? System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ADM", "downloads.json");
    }

    public string FilePath => _path;

    public List<DownloadRecord> Load()
    {
        try
        {
            if (!File.Exists(_path))
                return new List<DownloadRecord>();

            var json = File.ReadAllText(_path);
            return JsonSerializer.Deserialize<List<DownloadRecord>>(json, JsonOptions) ?? new List<DownloadRecord>();
        }
        catch (Exception)
        {
            // Try the previous good copy before giving up.
            var backup = _path + ".bak";
            try
            {
                if (File.Exists(backup))
                {
                    var json = File.ReadAllText(backup);
                    return JsonSerializer.Deserialize<List<DownloadRecord>>(json, JsonOptions) ?? new List<DownloadRecord>();
                }
            }
            catch (Exception)
            {
                // Ignore: fall through to an empty list.
            }

            return new List<DownloadRecord>();
        }
    }

    public void Save(IEnumerable<DownloadItem> items)
    {
        var directory = System.IO.Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var records = items.Select(DownloadRecord.FromItem).ToList();
        var json = JsonSerializer.Serialize(records, JsonOptions);
        var temp = _path + ".tmp";
        File.WriteAllText(temp, json);

        if (File.Exists(_path))
            File.Replace(temp, _path, _path + ".bak", ignoreMetadataErrors: true);
        else
            File.Move(temp, _path);
    }
}
