using System.Globalization;
using System.Security.Cryptography;

namespace GerenciamentoMecanica.Autenticacao.Infrastructure.Authentication;

internal static class CustomerAccessCode
{
    public static string Generate() => RandomNumberGenerator.GetInt32(100_000_000).ToString("D8", CultureInfo.InvariantCulture);

    public static bool IsWellFormed(string? code) => code is { Length: 8 } && code.All(character => character is >= '0' and <= '9');

    public static byte[] Derive(string code, byte[] salt) =>
        Rfc2898DeriveBytes.Pbkdf2(code, salt, 600_000, HashAlgorithmName.SHA256, 32);

    public static bool Matches(string code, byte[] salt, byte[] verifier)
    {
        var candidate = Derive(code, salt);
        try { return CryptographicOperations.FixedTimeEquals(candidate, verifier); }
        finally { CryptographicOperations.ZeroMemory(candidate); }
    }
}
