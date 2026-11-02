using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace Alcoler.Remote;

internal static class TokenHasher
{
    public static byte[] Hash(string token) => SHA256.HashData(Encoding.UTF8.GetBytes(token));

    public static string HashText(string token) => Convert.ToBase64String(Hash(token));

    public static bool Matches(byte[] computed, string storedBase64)
    {
        Span<byte> stored = stackalloc byte[32];
        return Convert.TryFromBase64String(storedBase64, stored, out var written)
            && written == stored.Length
            && CryptographicOperations.FixedTimeEquals(computed, stored);
    }

    public static bool FixedTimeTextEquals(string left, string right)
    {
        var a = Encoding.UTF8.GetBytes(left);
        var b = Encoding.UTF8.GetBytes(right);
        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }

    public static string NewToken(int byteLength) =>
        Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(byteLength));
}
