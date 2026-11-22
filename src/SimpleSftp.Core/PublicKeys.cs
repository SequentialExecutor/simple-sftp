using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace SimpleSftp.Core;

public sealed record ParsedPublicKey(string Algorithm, byte[] Blob, string Fingerprint);

public static class PublicKeys
{
    public static ParsedPublicKey Parse(string line)
    {
        if (string.IsNullOrWhiteSpace(line) || line.Length > 16384 || line.Contains("PRIVATE KEY", StringComparison.Ordinal))
            throw new ArgumentException("Paste an OpenSSH public key (.pub), never a private key.");
        var parts = line.Trim().Split((char[]?)null, 3, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) throw new ArgumentException("Use an OpenSSH public key: algorithm, base64 key, and optional comment.");
        try
        {
            var blob = Convert.FromBase64String(parts[1]);
            var offset = 0;
            byte[] Read()
            {
                if (offset + 4 > blob.Length) throw new ArgumentException("Invalid public-key encoding.");
                var length = BinaryPrimitives.ReadUInt32BigEndian(blob.AsSpan(offset, 4)); offset += 4;
                if (length > 8192 || length > blob.Length - offset) throw new ArgumentException("Invalid public-key encoding.");
                var value = blob.AsSpan(offset, (int)length).ToArray(); offset += (int)length; return value;
            }
            var algorithm = Encoding.ASCII.GetString(Read());
            if (algorithm != parts[0]) throw new ArgumentException("The public-key type does not match its contents.");
            if (algorithm == "ssh-rsa")
            {
                var exponent = Read().AsSpan().TrimStart((byte)0).ToArray();
                var modulus = Read().AsSpan().TrimStart((byte)0).ToArray();
                using var rsa = RSA.Create(); rsa.ImportParameters(new RSAParameters { Exponent = exponent, Modulus = modulus });
                if (rsa.KeySize < 2048 || rsa.KeySize > 8192) throw new ArgumentException("Use RSA keys between 2048 and 8192 bits.");
            }
            else if (algorithm.StartsWith("ecdsa-sha2-", StringComparison.Ordinal))
            {
                var curve = Encoding.ASCII.GetString(Read());
                var (named, size) = curve switch
                {
                    "nistp256" => (ECCurve.NamedCurves.nistP256, 32),
                    "nistp384" => (ECCurve.NamedCurves.nistP384, 48),
                    "nistp521" => (ECCurve.NamedCurves.nistP521, 66),
                    _ => throw new ArgumentException("Use an ECDSA P-256, P-384, or P-521 public key.")
                };
                if (algorithm != "ecdsa-sha2-" + curve) throw new ArgumentException("Invalid ECDSA curve.");
                var point = Read();
                if (point.Length != 1 + 2 * size || point[0] != 4) throw new ArgumentException("Invalid ECDSA public point.");
                using var ec = ECDsa.Create(new ECParameters { Curve = named, Q = new ECPoint { X = point[1..(1 + size)], Y = point[(1 + size)..] } });
            }
            else throw new ArgumentException("Supported public keys are ECDSA (P-256/P-384/P-521) and RSA with SHA-2 signatures. Ed25519 and certificates are not supported.");
            if (offset != blob.Length) throw new ArgumentException("Unexpected data after the public key.");
            return new(algorithm, blob, "SHA256:" + Convert.ToBase64String(SHA256.HashData(blob)).TrimEnd('='));
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        { throw new ArgumentException("The public key is malformed or unsupported.", ex); }
    }

    public static bool IsAuthorized(SftpUser? user, byte[] blob)
    {
        if (user is not { Enabled: true } || blob.Length > 16384) return false;
        return user.AuthorizedKeys.Any(line => CryptographicOperations.FixedTimeEquals(Parse(line).Blob, blob));
    }
}
