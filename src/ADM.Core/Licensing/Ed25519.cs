using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace Adm.Core.Licensing;

/// <summary>
/// Ed25519 signature verification (RFC 8032) written in plain C#, so the app
/// does not need a cryptography package. Only verification is implemented:
/// the app never creates a license token.
/// </summary>
public static class Ed25519
{
    // p = 2^255 - 19
    private static readonly BigInteger P = BigInteger.Pow(2, 255) - 19;

    // l = 2^252 + 27742317777372353535851937790883648493
    private static readonly BigInteger L =
        BigInteger.Pow(2, 252) + BigInteger.Parse("27742317777372353535851937790883648493");

    // d = -121665 / 121666 (mod p)
    private static readonly BigInteger D = Mod(-121665 * Inverse(121666));

    // sqrt(-1) (mod p)
    private static readonly BigInteger SqrtMinusOne = BigInteger.ModPow(2, (P - 1) / 4, P);

    // The standard base point: y = 4/5, x = 15112221... (the even root).
    private static readonly Point Base = CreateBasePoint();

    private static Point CreateBasePoint()
    {
        BigInteger x = BigInteger.Parse(
            "15112221349535400772501151409588531511454012693041857206046113283949847762202");
        BigInteger y = Mod(4 * Inverse(5));

        return new Point
        {
            X = x,
            Y = y,
            Z = BigInteger.One,
            T = Mod(x * y)
        };
    }

    /// <summary>
    /// Verifies a 64 byte Ed25519 signature over a message.
    /// </summary>
    public static bool Verify(ReadOnlySpan<byte> publicKey, ReadOnlySpan<byte> message,
        ReadOnlySpan<byte> signature)
    {
        if (publicKey.Length != 32 || signature.Length != 64)
            return false;

        try
        {
            if (!TryDecodePoint(publicKey, out Point? a))
                return false;

            if (!TryDecodePoint(signature[..32], out Point? r))
                return false;

            // S must be a canonical scalar.
            BigInteger s = LittleEndianToInteger(signature[32..]);
            if (s >= L)
                return false;

            // k = SHA-512(R || A || M) (mod l)
            byte[] hashInput = new byte[32 + 32 + message.Length];
            signature[..32].CopyTo(hashInput);
            publicKey.CopyTo(hashInput.AsSpan(32));
            message.CopyTo(hashInput.AsSpan(64));

            BigInteger k = Mod(LittleEndianToInteger(SHA512.HashData(hashInput)), L);

            // The signature is valid when [S]B = R + [k]A.
            Point left = ScalarMultiply(Base, s);
            Point right = Add(r!.Value, ScalarMultiply(a!.Value, k));

            return left.SameAs(right);
        }
        catch (Exception)
        {
            // A malformed key or signature must never crash the caller.
            return false;
        }
    }

    /// <summary>
    /// Extracts the 32 byte key from a PEM encoded SubjectPublicKeyInfo block.
    /// </summary>
    public static bool TryGetRawKey(string pem, out byte[] rawKey)
    {
        rawKey = Array.Empty<byte>();

        if (string.IsNullOrWhiteSpace(pem))
            return false;

        try
        {
            var builder = new StringBuilder(pem.Length);
            foreach (char ch in pem)
            {
                if (!char.IsWhiteSpace(ch))
                    builder.Append(ch);
            }

            string base64 = builder.ToString()
                .Replace("-----BEGINPUBLICKEY-----", string.Empty)
                .Replace("-----ENDPUBLICKEY-----", string.Empty)
                // A PEM body may use the url safe alphabet, which the plain
                // base64 decoder rejects.
                .Replace('-', '+')
                .Replace('_', '/');

            // The padding is often left out, but the decoder needs it.
            switch (base64.Length % 4)
            {
                case 2:
                    base64 += "==";
                    break;
                case 3:
                    base64 += "=";
                    break;
                case 1:
                    return false;
            }

            byte[] der;
            try
            {
                der = Convert.FromBase64String(base64);
            }
            catch (FormatException)
            {
                return false;
            }

            if (der.Length < 32)
                return false;

            // The Ed25519 SubjectPublicKeyInfo ends with the 32 byte key.
            byte[] key = der[^32..];

            // Check the algorithm identifier so a wrong key type is rejected.
            ReadOnlySpan<byte> prefix = der.AsSpan(0, Math.Min(12, der.Length - 32));
            bool ed25519 = false;
            for (int i = 0; i + 4 < prefix.Length; i++)
            {
                if (prefix[i] == 0x06 && prefix[i + 1] == 0x03 && prefix[i + 2] == 0x2B &&
                    prefix[i + 3] == 0x65 && prefix[i + 4] == 0x70)
                {
                    ed25519 = true;
                    break;
                }
            }

            if (!ed25519)
                return false;

            rawKey = key;
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    // ----------------------------------------------------------------- points

    /// <summary>A point in extended twisted Edwards coordinates.</summary>
    private readonly struct Point
    {
        public BigInteger X { get; init; }
        public BigInteger Y { get; init; }
        public BigInteger Z { get; init; }
        public BigInteger T { get; init; }

        public static Point Identity => new()
        {
            X = BigInteger.Zero,
            Y = BigInteger.One,
            Z = BigInteger.One,
            T = BigInteger.Zero
        };

        /// <summary>True when both points describe the same curve point.</summary>
        public bool SameAs(Point other) =>
            Mod(X * other.Z) == Mod(other.X * Z) &&
            Mod(Y * other.Z) == Mod(other.Y * Z);
    }

    private static Point Add(Point p, Point q)
    {
        BigInteger a = Mod((p.Y - p.X) * (q.Y - q.X));
        BigInteger b = Mod((p.Y + p.X) * (q.Y + q.X));
        BigInteger c = Mod(p.T * 2 * D % P * q.T);
        BigInteger d = Mod(p.Z * 2 * q.Z);
        BigInteger e = Mod(b - a);
        BigInteger f = Mod(d - c);
        BigInteger g = Mod(d + c);
        BigInteger h = Mod(b + a);

        return new Point
        {
            X = Mod(e * f),
            Y = Mod(g * h),
            T = Mod(e * h),
            Z = Mod(f * g)
        };
    }

    private static Point Double(Point p)
    {
        BigInteger a = Mod(p.X * p.X);
        BigInteger b = Mod(p.Y * p.Y);
        BigInteger c = Mod(2 * p.Z * p.Z);
        BigInteger d = Mod(-a);
        BigInteger e = Mod((p.X + p.Y) * (p.X + p.Y) - a - b);
        BigInteger g = Mod(d + b);
        BigInteger f = Mod(g - c);
        BigInteger h = Mod(d - b);

        return new Point
        {
            X = Mod(e * f),
            Y = Mod(g * h),
            T = Mod(e * h),
            Z = Mod(f * g)
        };
    }

    private static Point ScalarMultiply(Point point, BigInteger scalar)
    {
        Point result = Point.Identity;
        Point addend = point;

        for (int bit = 0; bit < scalar.GetBitLength(); bit++)
        {
            if (((scalar >> bit) & BigInteger.One) == BigInteger.One)
                result = Add(result, addend);

            addend = Double(addend);
        }

        return result;
    }

    /// <summary>Decodes a 32 byte compressed point.</summary>
    private static bool TryDecodePoint(ReadOnlySpan<byte> encoded, out Point? point)
    {
        point = null;

        if (encoded.Length != 32)
            return false;

        byte[] copy = encoded.ToArray();
        int sign = (copy[31] >> 7) & 1;
        copy[31] &= 0x7F;

        BigInteger y = LittleEndianToInteger(copy);
        if (y >= P)
            return false;

        // x^2 = (y^2 - 1) / (d y^2 + 1)
        BigInteger y2 = Mod(y * y);
        BigInteger u = Mod(y2 - 1);
        BigInteger v = Mod(D * y2 + 1);

        BigInteger v3 = Mod(v * v * v);
        BigInteger v7 = Mod(v3 * v3 * v);
        BigInteger x = Mod(u * v3 * BigInteger.ModPow(Mod(u * v7), (P - 5) / 8, P));

        if (Mod(v * x * x) != u)
        {
            x = Mod(x * SqrtMinusOne);

            if (Mod(v * x * x) != u)
                return false;
        }

        if (x.IsZero && sign == 1)
            return false;

        if ((x.IsEven ? 0 : 1) != sign)
            x = Mod(P - x);

        point = new Point
        {
            X = x,
            Y = y,
            Z = BigInteger.One,
            T = Mod(x * y)
        };

        return true;
    }

    // -------------------------------------------------------------- helpers

    private static BigInteger Mod(BigInteger value)
    {
        BigInteger result = value % P;
        return result.Sign < 0 ? result + P : result;
    }

    private static BigInteger Mod(BigInteger value, BigInteger modulus)
    {
        BigInteger result = value % modulus;
        return result.Sign < 0 ? result + modulus : result;
    }

    private static BigInteger Inverse(BigInteger value) =>
        BigInteger.ModPow(Mod(value), P - 2, P);

    private static BigInteger LittleEndianToInteger(ReadOnlySpan<byte> bytes)
    {
        // BigInteger reads the array in little-endian order, which is exactly
        // the order the signature uses.
        return new BigInteger(bytes.ToArray(), isUnsigned: true, isBigEndian: false);
    }
}
