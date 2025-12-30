using System;
using System.Security.Cryptography;
using System.Text;

namespace CartSmart.API.Models;

public class PasswordHasher
{
    private const int SaltSize = 16; // 16 bytes = 128-bit salt
    private const int HashSize = 64; // 64 bytes = 512-bit hash
    private const int Iterations = 100000; // Recommended iteration count

    /// <summary>
    /// Hashes a password using PBKDF2 with a random salt.
    /// </summary>
    public static async Task<(string Hash, string Salt)> HashPasswordAsync(string password)
    {
        using var rng = RandomNumberGenerator.Create();
        byte[] salt = new byte[SaltSize];
        rng.GetBytes(salt); // Generate a random salt

        using var pbkdf2 = new Rfc2898DeriveBytes(password, salt, Iterations, HashAlgorithmName.SHA512);
        byte[] hash = pbkdf2.GetBytes(HashSize); // Generate a 512-bit hash

        return (Convert.ToBase64String(hash), Convert.ToBase64String(salt));
    }

    /// <summary>
    /// Verifies if the given password matches the stored hash.
    /// </summary>
    public static async Task<bool> VerifyPasswordAsync(string password, string storedHash, string storedSalt)
    {
        byte[] saltBytes = Convert.FromBase64String(storedSalt);

        using var pbkdf2 = new Rfc2898DeriveBytes(password, saltBytes, Iterations, HashAlgorithmName.SHA512);
        byte[] computedHash = pbkdf2.GetBytes(HashSize);

        return Convert.ToBase64String(computedHash) == storedHash; // Compare hashes
    }
}
