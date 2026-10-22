using System.Net;
using System.Text;

namespace Adm.Core.Net;

/// <summary>Builds the proxy, credential and header pieces shared by every request.</summary>
public static class NetworkOptions
{
    /// <summary>Creates a proxy from the user's settings, or null when none is configured.</summary>
    public static IWebProxy? CreateProxy(string? proxyUrl, string? userName, string? password)
    {
        if (string.IsNullOrWhiteSpace(proxyUrl))
            return null;

        string url = proxyUrl.Trim();
        if (!url.Contains("://", StringComparison.Ordinal))
            url = "http://" + url;

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return null;

        var proxy = new WebProxy(uri, BypassOnLocal: false);

        if (!string.IsNullOrWhiteSpace(userName))
        {
            string credentials = string.IsNullOrEmpty(password)
                ? userName
                : userName + ":" + password;

            proxy.Credentials = new NetworkCredential(userName, password ?? string.Empty);
        }

        return proxy;
    }

    /// <summary>Builds the value for an <c>Authorization: Basic</c> header, or null.</summary>
    public static string? BuildBasicAuth(string? userName, string? password)
    {
        if (string.IsNullOrWhiteSpace(userName))
            return null;

        string pair = userName + ":" + (password ?? string.Empty);
        string encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(pair));
        return "Basic " + encoded;
    }
}
