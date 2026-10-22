using System.Text;
using System.Text.Json;

namespace Adm.Core.Licensing;

/// <summary>
/// A license token: a base64url payload and a base64url Ed25519 signature,
/// separated by a dot. The signature covers the payload segment exactly as it
/// appears in the token.
/// </summary>
public sealed class LicenseToken
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private LicenseToken(string payload, byte[] signature, LicenseClaims claims)
    {
        Payload = payload;
        Signature = signature;
        Claims = claims;
    }

    /// <summary>The base64url payload segment, exactly as it was signed.</summary>
    public string Payload { get; }

    /// <summary>The decoded signature.</summary>
    public byte[] Signature { get; }

    /// <summary>The claims carried by the payload.</summary>
    public LicenseClaims Claims { get; }

    /// <summary>The token in its wire format, ready to be stored.</summary>
    public string Encoded => Payload + "." + Base64UrlEncode(Signature);

    /// <summary>
    /// Splits a token into its parts. Returns null when the format is wrong or
    /// the payload is not valid JSON.
    /// </summary>
    public static LicenseToken? Parse(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
            return null;

        string text = token.Trim();
        int separator = text.IndexOf('.');
        if (separator <= 0 || separator >= text.Length - 1)
            return null;

        string payload = text[..separator];
        string signature = text[(separator + 1)..];

        byte[] signatureBytes;
        try
        {
            signatureBytes = Base64UrlDecode(signature);
        }
        catch (FormatException)
        {
            return null;
        }

        if (signatureBytes.Length != 64)
            return null;

        LicenseClaims claims;
        try
        {
            string json = Encoding.UTF8.GetString(Base64UrlDecode(payload));
            claims = JsonSerializer.Deserialize<LicenseClaims>(json, JsonOptions) ?? new LicenseClaims();
        }
        catch (Exception)
        {
            return null;
        }

        return new LicenseToken(payload, signatureBytes, claims);
    }

    /// <summary>
    /// Checks the signature with the given public key. The public key can be a
    /// PEM block or the raw 32 bytes.
    /// </summary>
    public bool VerifySignature(string publicKeyPem)
    {
        if (!Ed25519.TryGetRawKey(publicKeyPem, out byte[] key))
            return false;

        return Ed25519.Verify(key, Encoding.UTF8.GetBytes(Payload), Signature);
    }

    /// <summary>Encodes bytes as base64url without padding.</summary>
    public static string Base64UrlEncode(byte[] data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>Decodes base64url, with or without padding.</summary>
    public static byte[] Base64UrlDecode(string data)
    {
        string text = data.Replace('-', '+').Replace('_', '/');
        int padding = text.Length % 4;
        if (padding == 2)
            text += "==";
        else if (padding == 3)
            text += "=";
        else if (padding == 1)
            throw new FormatException("Invalid base64url length.");

        return Convert.FromBase64String(text);
    }
}
