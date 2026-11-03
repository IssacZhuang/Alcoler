using System.Security.Cryptography;

namespace Alcoler.Remote;

public static class RemoteConnectionCode
{
    private const int GeneratedLength = 8;
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    public static string Generate() =>
        new(RandomNumberGenerator.GetItems<char>(Alphabet, GeneratedLength));

    public static bool IsValid(string? code)
    {
        if (code is null || code.Length is < 8 or > 64)
            return false;
        foreach (var character in code)
        {
            if (!char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_'))
                return false;
        }
        return true;
    }
}
