namespace Adm.Core.Licensing;

/// <summary>Why a licence is not valid.</summary>
public enum LicenseState
{
    /// <summary>No licence has been activated yet.</summary>
    NotActivated = 0,

    /// <summary>The licence is valid and the token is still fresh.</summary>
    Active = 1,

    /// <summary>The licence has expired.</summary>
    Expired = 2,

    /// <summary>The licence was suspended by the administrator.</summary>
    Suspended = 3,

    /// <summary>The key reached the number of devices it allows.</summary>
    DeviceLimit = 4,

    /// <summary>This device was removed from the licence.</summary>
    RevokedDevice = 5,

    /// <summary>The key is not valid for this app.</summary>
    Invalid = 6,

    /// <summary>The licence could not be confirmed because the server is unreachable.</summary>
    Unreachable = 7,

    /// <summary>The stored token is not valid.</summary>
    Tampered = 8
}

/// <summary>The outcome of asking the server for a licence key.</summary>
public sealed class LicenseRequestResult
{
    public bool Ok { get; init; }

    /// <summary>The key, when the server handed one out straight away.</summary>
    public string? Key { get; init; }

    /// <summary>A message for the user.</summary>
    public string? Error { get; init; }

    public static LicenseRequestResult Failed(string error) => new() { Ok = false, Error = error };
}

/// <summary>The result of a licence check.</summary>
public sealed class LicenseStatus
{
    public LicenseState State { get; init; } = LicenseState.NotActivated;

    /// <summary>True when the app may run.</summary>
    public bool IsLicensed => State == LicenseState.Active;

    /// <summary>True when the licence is fine but the server could not be reached.</summary>
    public bool IsGrace => State == LicenseState.Unreachable;

    /// <summary>A message that explains the state to the user.</summary>
    public string Message { get; init; } = string.Empty;

    /// <summary>When the licence expires, or null when it does not.</summary>
    public DateTimeOffset? ExpiresAt { get; init; }

    /// <summary>When the token asks for the next online check.</summary>
    public DateTimeOffset? RecheckAt { get; init; }

    /// <summary>The key that was used.</summary>
    public string? Key { get; init; }

    /// <summary>The device this licence is bound to.</summary>
    public string DeviceId { get; init; } = string.Empty;

    /// <summary>True when the app may run, including during a grace period.</summary>
    public bool CanRun => IsLicensed || IsGrace;

    /// <summary>A short label for the status line.</summary>
    public string SummaryText => State switch
    {
        LicenseState.Active => "Licensed",
        LicenseState.NotActivated => "Not activated",
        LicenseState.Expired => "Expired",
        LicenseState.Suspended => "Suspended",
        LicenseState.DeviceLimit => "Device limit reached",
        LicenseState.RevokedDevice => "This device was revoked",
        LicenseState.Invalid => "Invalid key",
        LicenseState.Unreachable => "Cannot reach the licence server",
        LicenseState.Tampered => "The licence data was modified",
        _ => State.ToString()
    };

    /// <summary>Maps a status the server reported to a licence state.</summary>
    public static LicenseState FromServerStatus(string? status) => (status ?? string.Empty).Trim()
        .ToLowerInvariant() switch
        {
            "expired" => LicenseState.Expired,
            "suspended" => LicenseState.Suspended,
            "device_limit" => LicenseState.DeviceLimit,
            "revoked_device" => LicenseState.RevokedDevice,
            "invalid" => LicenseState.Invalid,
            _ => LicenseState.Invalid
        };
}
