using System.Text.Json;

namespace Adm.Core.Licensing;

/// <summary>Persists the one-time, seven-day local evaluation period.</summary>
public sealed class LicenseTrialStore
{
    public static readonly TimeSpan TrialLength = TimeSpan.FromDays(7);

    private readonly string _path;
    private readonly string _deviceId;

    public LicenseTrialStore(string deviceId, string? path = null)
    {
        _deviceId = deviceId;
        _path = path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ADM", "trial.json");
    }

    public LicenseTrialStatus GetStatus(DateTimeOffset? now = null)
    {
        DateTimeOffset current = now ?? DateTimeOffset.Now;
        TrialRecord? record;
        try
        {
            if (!File.Exists(_path))
            {
                record = new TrialRecord { DeviceId = _deviceId, StartedAt = current, LastSeenAt = current };
                if (!Save(record))
                    return LicenseTrialStatus.Expired;
            }
            else
            {
                record = JsonSerializer.Deserialize<TrialRecord>(File.ReadAllText(_path));
            }
        }
        catch (Exception)
        {
            // A corrupt trial record cannot safely grant a fresh trial.
            return LicenseTrialStatus.Expired;
        }

        if (record is null || record.StartedAt == default ||
            !string.Equals(record.DeviceId, _deviceId, StringComparison.OrdinalIgnoreCase) ||
            current < record.LastSeenAt)
            return LicenseTrialStatus.Expired;

        DateTimeOffset endsAt = record.StartedAt + TrialLength;
        if (current >= endsAt)
            return new LicenseTrialStatus(false, endsAt, TimeSpan.Zero);

        record.LastSeenAt = current;
        if (!Save(record))
            return LicenseTrialStatus.Expired;
        return new LicenseTrialStatus(true, endsAt, endsAt - current);
    }

    private bool Save(TrialRecord record)
    {
        try
        {
            string? directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);
            string temporary = _path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(record));
            File.Move(temporary, _path, overwrite: true);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private sealed class TrialRecord
    {
        public string DeviceId { get; set; } = string.Empty;
        public DateTimeOffset StartedAt { get; set; }
        public DateTimeOffset LastSeenAt { get; set; }
    }
}

public sealed record LicenseTrialStatus(bool IsActive, DateTimeOffset? EndsAt, TimeSpan Remaining)
{
    public static LicenseTrialStatus Expired { get; } = new(false, null, TimeSpan.Zero);
}
