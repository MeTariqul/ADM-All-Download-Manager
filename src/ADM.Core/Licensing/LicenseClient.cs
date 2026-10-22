using System.Text;
using System.Text.Json;

namespace Adm.Core.Licensing;

/// <summary>The outcome of a call to the license server.</summary>
public sealed class LicenseResponse
{
    public bool Ok { get; init; }

    /// <summary>The status the server reported, for example expired or suspended.</summary>
    public string? Status { get; init; }

    /// <summary>A message meant for the user.</summary>
    public string? Error { get; init; }

    /// <summary>The signed license token.</summary>
    public string? Token { get; init; }

    /// <summary>True when the failure is a temporary network problem.</summary>
    public bool IsNetworkError { get; init; }

    public static LicenseResponse Failure(string status, string error, bool network = false) =>
        new() { Ok = false, Status = status, Error = error, IsNetworkError = network };
}

/// <summary>Talks to the license server over HTTPS.</summary>
public sealed class LicenseClient
{
    public const string DefaultBaseUrl = "https://metariqul.vercel.app/api/license";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _http;

    public LicenseClient(string? appKey = null, string? baseUrl = null, HttpClient? http = null)
    {
        AppKey = string.IsNullOrWhiteSpace(appKey)
            ? LicenseConfiguration.AppKey
            : appKey.Trim();

        BaseUrl = string.IsNullOrWhiteSpace(baseUrl) ? DefaultBaseUrl : baseUrl.TrimEnd('/');

        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
    }

    /// <summary>The app key sent in the x-app-key header.</summary>
    public string AppKey { get; }

    /// <summary>The base address of the license API.</summary>
    public string BaseUrl { get; }

    /// <summary>Activates a license key for this device and returns a token.</summary>
    public async Task<LicenseResponse> ActivateAsync(string key, string deviceId,
        CancellationToken cancellationToken = default)
    {
        var payload = new Dictionary<string, string>
        {
            ["key"] = key,
            ["deviceId"] = deviceId,
            ["platform"] = "win32"
        };

        return await PostAsync("activate", payload, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Re-checks a license key and returns a fresh token.</summary>
    public async Task<LicenseResponse> VerifyAsync(string key, string deviceId,
        CancellationToken cancellationToken = default)
    {
        var payload = new Dictionary<string, string>
        {
            ["key"] = key,
            ["deviceId"] = deviceId
        };

        return await PostAsync("verify", payload, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Asks the server for a licence key for an address. The dashboard can be
    /// set to hand the key out straight away, so the reply may already carry
    /// it; otherwise it arrives by mail.
    /// </summary>
    public async Task<LicenseRequestResult> RequestKeyAsync(string email,
        CancellationToken cancellationToken = default)
    {
        var payload = new Dictionary<string, string> { ["email"] = email };

        try
        {
            string json = JsonSerializer.Serialize(payload);

            using var request = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/request")
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };

            AddHeaders(request);

            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(body))
                return LicenseRequestResult.Failed("The licence server returned an empty answer.");

            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;

            bool ok = root.TryGetProperty("ok", out var okValue) && okValue.ValueKind == JsonValueKind.True;
            string? key = root.TryGetProperty("key", out var keyValue) ? keyValue.GetString() : null;
            string? error = root.TryGetProperty("error", out var errorValue) ? errorValue.GetString() : null;

            if (!ok)
                return LicenseRequestResult.Failed(error ?? "The licence server refused the request.");

            return new LicenseRequestResult
            {
                Ok = true,
                Key = string.IsNullOrWhiteSpace(key) ? null : key
            };
        }
        catch (OperationCanceledException)
        {
            return LicenseRequestResult.Failed("The request to the licence server timed out.");
        }
        catch (Exception ex)
        {
            return LicenseRequestResult.Failed("The licence server could not be reached: " + ex.Message);
        }
    }

    /// <summary>Fetches the public key that signs the license tokens.</summary>
    public async Task<(bool Ok, string PublicKey, int RecheckDays, string? Error)> GetPublicKeyAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/pubkey");
            AddHeaders(request);

            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
                return (false, string.Empty, 0, "The license server could not be reached.");

            using var document = JsonDocument.Parse(body);
            string publicKey = document.RootElement.TryGetProperty("publicKey", out var key)
                ? key.GetString() ?? string.Empty
                : string.Empty;

            int days = document.RootElement.TryGetProperty("recheckDays", out var recheck) &&
                       recheck.TryGetInt32(out int value)
                ? value
                : 7;

            return (true, publicKey, days, null);
        }
        catch (Exception ex)
        {
            return (false, string.Empty, 0, ex.Message);
        }
    }

    private async Task<LicenseResponse> PostAsync(string action, Dictionary<string, string> payload,
        CancellationToken cancellationToken)
    {
        try
        {
            string json = JsonSerializer.Serialize(payload);

            using var request = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/{action}")
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };

            AddHeaders(request);

            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(body))
                return LicenseResponse.Failure("unknown", "The license server returned an empty answer.");

            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;

            bool ok = root.TryGetProperty("ok", out var okValue) && okValue.ValueKind == JsonValueKind.True;
            string? status = root.TryGetProperty("status", out var statusValue)
                ? statusValue.GetString()
                : null;
            string? error = root.TryGetProperty("error", out var errorValue)
                ? errorValue.GetString()
                : null;
            string? token = root.TryGetProperty("token", out var tokenValue)
                ? tokenValue.GetString()
                : null;

            if (ok && !string.IsNullOrWhiteSpace(token))
                return new LicenseResponse { Ok = true, Token = token, Status = status };

            return LicenseResponse.Failure(status ?? "invalid", error ?? "The license server rejected the request.");
        }
        catch (OperationCanceledException)
        {
            return LicenseResponse.Failure("network", "The request to the license server timed out.", network: true);
        }
        catch (Exception ex)
        {
            return LicenseResponse.Failure("network", "The license server could not be reached: " + ex.Message, network: true);
        }
    }

    private void AddHeaders(HttpRequestMessage request)
    {
        if (!string.IsNullOrWhiteSpace(AppKey))
            request.Headers.TryAddWithoutValidation("x-app-key", AppKey);
    }
}
