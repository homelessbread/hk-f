using System.Security.Cryptography;

namespace HotelKeeper.Data;

public static class Passwords
{
    const int Iterations = 100_000;

    public static string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, 32);
        return $"{Iterations}.{Convert.ToBase64String(salt)}.{Convert.ToBase64String(hash)}";
    }

    public static bool Verify(string password, string stored)
    {
        var p = stored.Split('.');
        if (p.Length != 3 || !int.TryParse(p[0], out var iterations)) return false;
        var salt = Convert.FromBase64String(p[1]);
        var expected = Convert.FromBase64String(p[2]);
        var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    public static string? Validate(string password)
    {
        if (password.Length < 10 || !password.Any(char.IsUpper) || !password.Any(char.IsLower) || !password.Any(char.IsDigit))
            return "Password needs 10+ characters with upper case, lower case and a digit.";
        return null;
    }

    /// <summary>Random temporary password that satisfies <see cref="Validate"/>.</summary>
    public static string Generate()
    {
        const string upper = "ABCDEFGHJKLMNPQRSTUVWXYZ", lower = "abcdefghijkmnpqrstuvwxyz", digits = "23456789";
        var all = upper + lower + digits;
        var chars = new List<char> { Pick(upper), Pick(lower), Pick(digits) };
        while (chars.Count < 12) chars.Add(Pick(all));
        return new string(chars.OrderBy(_ => RandomNumberGenerator.GetInt32(int.MaxValue)).ToArray());

        static char Pick(string s) => s[RandomNumberGenerator.GetInt32(s.Length)];
    }
}
