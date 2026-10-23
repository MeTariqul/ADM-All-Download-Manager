using Adm.Core.Util;

namespace Adm.Core.Licensing;

/// <summary>
/// Owns the licence state of the app. The token is verified offline with the
/// shipped public key, and the server is only contacted when the token asks
/// for a re-check or when a licence is activated.
/// </summary>
public sealed class LicenseService
{
    private readonly LicenseStore _store;
    private readonly LicenseClient _client;
    private readonly string _publicKeyPem;
    private readonly string _deviceId;
    private readonly LicenseTrialStore _trialStore;

    private LicenseStatus _status = new();

    public LicenseService(LicenseStore? store = null, LicenseClient? client = null,
        string? publicKeyPem = null, string? deviceId = null)
    {
        _store = store ?? new LicenseStore();
        _client = client ?? new LicenseClient(LicenseConfiguration.EffectiveAppKey);
        _publicKeyPem = publicKeyPem ?? LicenseConfiguration.PublicKeyPem;
        _deviceId = string.IsNullOrWhiteSpace(deviceId) ? DeviceIdentity.DeviceId : deviceId;
        _trialStore = new LicenseTrialStore(_deviceId);
    }

    /// <summary>The most recent licence status.</summary>
    public LicenseStatus Status => _status;

    /// <summary>The device id this app uses.</summary>
    public string DeviceId => _deviceId;

    /// <summary>Returns the persistent trial status, starting it on first use.</summary>
    public LicenseTrialStatus GetTrialStatus() => _trialStore.GetStatus();

    /// <summary>Requests a licence key using the configured licensing service.</summary>
    public Task<LicenseRequestResult> RequestKeyAsync(string email,
        CancellationToken cancellationToken = default) => _client.RequestKeyAsync(email, cancellationToken);

    /// <summary>The key that is currently stored, if any.</summary>
    public string? StoredKey => _store.Load()?.Key;

    /// <summary>
    /// Checks the stored licence. The token is verified without the network;
    /// the server is only asked when the token asks for it.
    /// </summary>
    public async Task<LicenseStatus> CheckAsync(CancellationToken cancellationToken = default)
    {
        var record = _store.Load();

        if (record is null || string.IsNullOrWhiteSpace(record.Token) || string.IsNullOrWhiteSpace(record.Key))
        {
            _status = new LicenseStatus
            {
                State = LicenseState.NotActivated,
                Message = "This copy of ADM is not activated yet.",
                DeviceId = DeviceId
            };

            return _status;
        }

        var token = LicenseToken.Parse(record.Token);
        if (token is null)
        {
            _status = new LicenseStatus
            {
                State = LicenseState.Tampered,
                Message = "The stored licence could not be read.",
                Key = record.Key,
                DeviceId = DeviceId
            };

            return _status;
        }

        if (!token.VerifySignature(_publicKeyPem))
        {
            _status = new LicenseStatus
            {
                State = LicenseState.Tampered,
                Message = "The licence signature is not valid.",
                Key = record.Key,
                DeviceId = DeviceId
            };

            return _status;
        }

        // The token must belong to this machine.
        if (!string.IsNullOrEmpty(token.Claims.DeviceId) &&
            !string.Equals(token.Claims.DeviceId, DeviceId, StringComparison.OrdinalIgnoreCase))
        {
            _status = new LicenseStatus
            {
                State = LicenseState.RevokedDevice,
                Message = "This licence was issued for a different device.",
                Key = record.Key,
                DeviceId = DeviceId,
                ExpiresAt = token.Claims.ExpiresAtDate
            };

            return _status;
        }

        if (token.Claims.IsExpired(DateTimeOffset.Now))
        {
            _status = new LicenseStatus
            {
                State = LicenseState.Expired,
                Message = "This licence has expired.",
                Key = record.Key,
                DeviceId = DeviceId,
                ExpiresAt = token.Claims.ExpiresAtDate
            };

            return _status;
        }

        // The token is still good, but the server wants to confirm it.
        if (token.Claims.NeedsRecheck(DateTimeOffset.Now))
        {
            var response = await _client.VerifyAsync(record.Key!, DeviceId, cancellationToken).ConfigureAwait(false);

            if (response.Ok && !string.IsNullOrWhiteSpace(response.Token))
            {
                _store.Save(LicenseRecord.FromItem(record.Key!, response.Token!));

                var refreshed = LicenseToken.Parse(response.Token);
                _status = new LicenseStatus
                {
                    State = LicenseState.Active,
                    Message = "Licence confirmed.",
                    Key = record.Key,
                    DeviceId = DeviceId,
                    ExpiresAt = refreshed?.Claims.ExpiresAtDate,
                    RecheckAt = refreshed?.Claims.RecheckAtDate
                };

                DiagnosticsLog.Info("The licence was confirmed online.");
                return _status;
            }

            if (response.IsNetworkError)
            {
                // The server cannot be reached. The signed token is still valid
                // until it expires, so the app keeps working until then.
                _status = new LicenseStatus
                {
                    State = LicenseState.Unreachable,
                    Message = "The licence server could not be reached. ADM keeps working until the licence expires.",
                    Key = record.Key,
                    DeviceId = DeviceId,
                    ExpiresAt = token.Claims.ExpiresAtDate,
                    RecheckAt = token.Claims.RecheckAtDate
                };

                DiagnosticsLog.Info("The licence server could not be reached: " + response.Error);
                return _status;
            }

            _status = new LicenseStatus
            {
                State = LicenseStatus.FromServerStatus(response.Status),
                Message = response.Error ?? "The licence is no longer valid.",
                Key = record.Key,
                DeviceId = DeviceId,
                ExpiresAt = token.Claims.ExpiresAtDate
            };

            DiagnosticsLog.Warn("The licence was refused by the server: " + _status.Message);
            return _status;
        }

        _status = new LicenseStatus
        {
            State = LicenseState.Active,
            Message = "Licence verified offline.",
            Key = record.Key,
            DeviceId = DeviceId,
            ExpiresAt = token.Claims.ExpiresAtDate,
            RecheckAt = token.Claims.RecheckAtDate
        };

        return _status;
    }

    /// <summary>Activates a licence key and stores the returned token.</summary>
    public async Task<LicenseStatus> ActivateAsync(string key, CancellationToken cancellationToken = default)
    {
        string trimmed = (key ?? string.Empty).Trim();

        if (trimmed.Length == 0)
        {
            _status = new LicenseStatus
            {
                State = LicenseState.Invalid,
                Message = "Enter a licence key.",
                DeviceId = DeviceId
            };

            return _status;
        }

        var response = await _client.ActivateAsync(trimmed, DeviceId, cancellationToken).ConfigureAwait(false);

        if (!response.Ok || string.IsNullOrWhiteSpace(response.Token))
        {
            string message = response.Error ?? "The licence key was refused.";

            // The server answers a wrong licence key and a wrong app key with
            // the same message, so both are named to save the user a guess.
            if (string.Equals(response.Status, "invalid", StringComparison.OrdinalIgnoreCase))
            {
                message = "The licence server refused this key.";
            }

            _status = new LicenseStatus
            {
                State = response.IsNetworkError
                    ? LicenseState.Unreachable
                    : LicenseStatus.FromServerStatus(response.Status),
                Message = message,
                Key = trimmed,
                DeviceId = DeviceId
            };

            DiagnosticsLog.Warn("Activation failed: " + _status.Message);
            return _status;
        }

        // The token must be signed by the key that ships with the app.
        var token = LicenseToken.Parse(response.Token);
        if (token is null || !token.VerifySignature(_publicKeyPem))
        {
            _status = new LicenseStatus
            {
                State = LicenseState.Tampered,
                Message = "The licence server returned a token that is not correctly signed.",
                Key = trimmed,
                DeviceId = DeviceId
            };

            DiagnosticsLog.Error("The licence server returned an invalid token.");
            return _status;
        }

        _store.Save(LicenseRecord.FromItem(trimmed, response.Token!));

        _status = new LicenseStatus
        {
            State = LicenseState.Active,
            Message = "The licence was activated.",
            Key = trimmed,
            DeviceId = DeviceId,
            ExpiresAt = token.Claims.ExpiresAtDate,
            RecheckAt = token.Claims.RecheckAtDate
        };

        DiagnosticsLog.Info("The licence was activated for " + DeviceIdentity.ShortId + ".");
        return _status;
    }

    /// <summary>Removes the stored licence.</summary>
    public void Clear()
    {
        _store.Clear();
        _status = new LicenseStatus
        {
            State = LicenseState.NotActivated,
            Message = "This copy of ADM is not activated yet.",
            DeviceId = DeviceId
        };
    }
}
