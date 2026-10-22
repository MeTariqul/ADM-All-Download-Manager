using System.Security.Cryptography;

namespace Adm.Core.Util;

/// <summary>Hash algorithms offered for verifying a finished download.</summary>
public enum ChecksumAlgorithm
{
    None = 0,
    Md5 = 1,
    Sha1 = 2,
    Sha256 = 3
}

/// <summary>Computes file hashes so a download can be checked against a published checksum.</summary>
public static class Checksum
{
    /// <summary>Parses a user supplied algorithm name such as "sha256" or "SHA-256".</summary>
    public static ChecksumAlgorithm ParseAlgorithm(string? value)
    {
        return (value ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "md5" => ChecksumAlgorithm.Md5,
            "sha1" => ChecksumAlgorithm.Sha1,
            "sha-1" => ChecksumAlgorithm.Sha1,
            "sha256" => ChecksumAlgorithm.Sha256,
            "sha-256" => ChecksumAlgorithm.Sha256,
            _ => ChecksumAlgorithm.None
        };
    }

    public static string AlgorithmName(ChecksumAlgorithm algorithm) => algorithm switch
    {
        ChecksumAlgorithm.Md5 => "MD5",
        ChecksumAlgorithm.Sha1 => "SHA1",
        ChecksumAlgorithm.Sha256 => "SHA256",
        _ => ""
    };

    /// <summary>Computes the hash of a file and returns it as lower-case hex.</summary>
    public static string Compute(string path, ChecksumAlgorithm algorithm)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            64 * 1024, FileOptions.SequentialScan);

        using HashAlgorithm hasher = algorithm switch
        {
            ChecksumAlgorithm.Md5 => MD5.Create(),
            ChecksumAlgorithm.Sha1 => SHA1.Create(),
            ChecksumAlgorithm.Sha256 => SHA256.Create(),
            _ => throw new ArgumentOutOfRangeException(nameof(algorithm), algorithm, "Unsupported algorithm.")
        };

        return Convert.ToHexString(hasher.ComputeHash(stream)).ToLowerInvariant();
    }

    /// <summary>Compares a computed hash with an expected value, ignoring case and separators.</summary>
    public static bool Matches(string? expected, string actual)
    {
        if (string.IsNullOrWhiteSpace(expected))
            return false;

        string left = Normalize(expected);
        string right = Normalize(actual);
        return left.Length == right.Length && string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    }

    private static string Normalize(string value)
    {
        var builder = new System.Text.StringBuilder(value.Length);
        foreach (char ch in value)
        {
            if (char.IsLetterOrDigit(ch))
                builder.Append(ch);
        }

        return builder.ToString();
    }
}
