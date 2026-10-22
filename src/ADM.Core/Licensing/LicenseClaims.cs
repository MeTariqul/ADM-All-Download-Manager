using System.Text.Json;
using System.Text.Json.Serialization;

namespace Adm.Core.Licensing;

/// <summary>The claims inside a license token.</summary>
public sealed class LicenseClaims
{
    /// <summary>The device the license is bound to.</summary>
    [JsonPropertyName("d")]
    public string? DeviceId { get; set; }

    /// <summary>When the license expires, in Unix milliseconds. Zero means no expiry.</summary>
    [JsonPropertyName("e")]
    public long ExpiresAt { get; set; }

    /// <summary>When the token asks for an online re-check, in Unix milliseconds.</summary>
    [JsonPropertyName("r")]
    public long RecheckAt { get; set; }

    /// <summary>When the token was issued, in Unix milliseconds.</summary>
    [JsonPropertyName("i")]
    public long IssuedAt { get; set; }

    /// <summary>The license key the token belongs to.</summary>
    [JsonPropertyName("k")]
    public string? Key { get; set; }

    /// <summary>Any other claim the server sends.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement> Extra { get; set; } = new();

    /// <summary>True when the token has an expiry that is in the past.</summary>
    public bool IsExpired(DateTimeOffset now) =>
        ExpiresAt != 0 && ExpiresAt <= now.ToUnixTimeMilliseconds();

    /// <summary>True when the token asks for an online re-check.</summary>
    public bool NeedsRecheck(DateTimeOffset now) =>
        RecheckAt != 0 && RecheckAt <= now.ToUnixTimeMilliseconds();

    /// <summary>The expiry as a date, or null when the license never expires.</summary>
    public DateTimeOffset? ExpiresAtDate =>
        ExpiresAt == 0 ? null : DateTimeOffset.FromUnixTimeMilliseconds(ExpiresAt);

    /// <summary>The next re-check as a date, or null when there is none.</summary>
    public DateTimeOffset? RecheckAtDate =>
        RecheckAt == 0 ? null : DateTimeOffset.FromUnixTimeMilliseconds(RecheckAt);
}
