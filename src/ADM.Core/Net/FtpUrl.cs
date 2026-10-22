namespace Adm.Core.Net;

/// <summary>The parts of an FTP address.</summary>
public sealed record FtpAddress(string Url, string UrlHost, int UrlPort, string? UserName, string? Password, string Path)
{
    public bool IsSecure => Url.StartsWith("ftps://", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Reads FTP addresses, including the user name and password.</summary>
public static class FtpUrl
{
    /// <summary>
    /// Parses an ftp:// or ftps:// address. The user name and password may be
    /// part of the address; they are also accepted as separate parts.
    /// </summary>
    public static bool TryParse(string? url, out FtpAddress result)
    {
        result = new FtpAddress(string.Empty, string.Empty, 21, null, null, string.Empty);

        if (string.IsNullOrWhiteSpace(url))
            return false;

        string text = url.Trim();

        if (!text.StartsWith("ftp://", StringComparison.OrdinalIgnoreCase) &&
            !text.StartsWith("ftps://", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri))
            return false;

        string? userName = null;
        string? password = null;

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            int separator = uri.UserInfo.IndexOf(':');
            if (separator >= 0)
            {
                userName = Uri.UnescapeDataString(uri.UserInfo[..separator]);
                password = Uri.UnescapeDataString(uri.UserInfo[(separator + 1)..]);
            }
            else
            {
                userName = Uri.UnescapeDataString(uri.UserInfo);
            }
        }

        string path = Uri.UnescapeDataString(uri.AbsolutePath);

        bool secure = text.StartsWith("ftps://", StringComparison.OrdinalIgnoreCase);
        int port = uri.IsDefaultPort ? (secure ? 990 : 21) : uri.Port;

        result = new FtpAddress(text, uri.Host, port, userName, password, path);
        return true;
    }

    /// <summary>True when the address points at an FTP server.</summary>
    public static bool IsFtpUrl(string? url) =>
        !string.IsNullOrWhiteSpace(url) &&
        (url.TrimStart().StartsWith("ftp://", StringComparison.OrdinalIgnoreCase) ||
         url.TrimStart().StartsWith("ftps://", StringComparison.OrdinalIgnoreCase));
}
