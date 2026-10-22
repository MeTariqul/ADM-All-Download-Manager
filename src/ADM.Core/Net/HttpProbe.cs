using System.Net;
using System.Net.Http.Headers;

namespace Adm.Core.Net;

/// <summary>What the server told us before the real transfer starts.</summary>
public sealed record ProbeResult
{
    public string FinalUrl { get; init; } = string.Empty;

    /// <summary>Total size in bytes, or -1 when the server does not report one.</summary>
    public long TotalBytes { get; init; } = -1;

    public bool SupportsRanges { get; init; }

    public string? ETag { get; init; }

    public string? LastModified { get; init; }

    public string? ContentType { get; init; }

    public string? ContentDispositionFileName { get; init; }

    public string? ContentDispositionFileNameStar { get; init; }
}

/// <summary>
/// Small HTTP helper that asks a server whether it supports byte ranges.
/// Uses a one-byte ranged GET because many servers reject or misreport HEAD.
/// </summary>
public static class HttpProbe
{
    public static HttpClient CreateClient(int connections = 16, string? proxyUrl = null,
        string? proxyUser = null, string? proxyPassword = null)
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 10,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = TimeSpan.FromSeconds(20),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            MaxConnectionsPerServer = Math.Max(4, connections + 4)
        };

        IWebProxy? proxy = NetworkOptions.CreateProxy(proxyUrl, proxyUser, proxyPassword);
        if (proxy is not null)
        {
            handler.UseProxy = true;
            handler.Proxy = proxy;
        }

        return new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
    }

    public static HttpRequestMessage CreateRequest(string url, string? userAgent, string? referrer,
        RangeHeaderValue? range = null, RangeConditionHeaderValue? ifRange = null,
        string? authorization = null, string? cookie = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (!string.IsNullOrWhiteSpace(userAgent))
            request.Headers.TryAddWithoutValidation("User-Agent", userAgent);
        if (!string.IsNullOrWhiteSpace(referrer))
            request.Headers.TryAddWithoutValidation("Referer", referrer);
        if (!string.IsNullOrWhiteSpace(authorization))
            request.Headers.TryAddWithoutValidation("Authorization", authorization);
        if (!string.IsNullOrWhiteSpace(cookie))
            request.Headers.TryAddWithoutValidation("Cookie", cookie);
        request.Headers.TryAddWithoutValidation("Accept", "*/*");
        request.Headers.TryAddWithoutValidation("Accept-Encoding", "identity");

        if (range is not null)
            request.Headers.Range = range;
        if (ifRange is not null)
            request.Headers.IfRange = ifRange;

        return request;
    }

    public static async Task<ProbeResult> ProbeAsync(HttpClient client, string url, string? userAgent,
        string? referrer, CancellationToken cancellationToken, string? authorization = null, string? cookie = null)
    {
        using var request = CreateRequest(url, userAgent, referrer, new RangeHeaderValue(0, 0),
            null, authorization, cookie);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        var finalUrl = response.RequestMessage?.RequestUri?.ToString() ?? url;
        var disposition = response.Content.Headers.ContentDisposition;

        var baseResult = new ProbeResult
        {
            FinalUrl = finalUrl,
            ETag = response.Headers.ETag?.ToString(),
            LastModified = response.Content.Headers.LastModified?.ToString("R"),
            ContentType = response.Content.Headers.ContentType?.MediaType,
            ContentDispositionFileName = disposition?.FileName?.Trim('"'),
            ContentDispositionFileNameStar = disposition?.FileNameStar?.Trim('"'),
            TotalBytes = -1,
            SupportsRanges = false
        };

        if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            var contentRange = response.Content.Headers.ContentRange;
            long emptyLength = contentRange?.Length ?? 0;
            return baseResult with { TotalBytes = emptyLength, SupportsRanges = true };
        }

        if (response.StatusCode == HttpStatusCode.PartialContent)
        {
            long total = response.Content.Headers.ContentRange?.Length ?? -1;
            if (total < 0)
                total = response.Content.Headers.ContentLength ?? -1;

            return baseResult with { TotalBytes = total, SupportsRanges = true };
        }

        if (!response.IsSuccessStatusCode)
        {
            response.EnsureSuccessStatusCode();
        }

        // HTTP 200 means the server ignored Range: fall back to a single stream.
        return baseResult with
        {
            TotalBytes = response.Content.Headers.ContentLength ?? -1,
            SupportsRanges = false
        };
    }
}
