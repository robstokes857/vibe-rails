namespace VibeRails.Auth
{
    using System;
    using System.Security.Cryptography;

    public static class Hasher
    {
        // Per your request
        public const int Iterations = 200_000;
        public const int SaltSizeBytes = 32; // 256-bit salt
        public const int HashSizeBytes = 64; // 512-bit derived key

        /// <summary>
        /// Hashes an input string using PBKDF2-HMAC-SHA512 with a random 256-bit salt and 200,000 iterations.
        /// Returns Base64 strings for easy storage.
        /// </summary>
        public static (string SaltBase64, string HashBase64) Hash(string input)
        {
            ArgumentNullException.ThrowIfNullOrWhiteSpace(input);

            byte[] salt = new byte[SaltSizeBytes];
            RandomNumberGenerator.Fill(salt);

            byte[] hash = DeriveKey(input, salt);

            return (Convert.ToBase64String(salt), Convert.ToBase64String(hash));
        }

        /// <summary>
        /// Verifies an input string against a stored Base64 salt and Base64 hash using the same PBKDF2 parameters.
        /// Uses constant-time comparison to mitigate timing attacks.
        /// </summary>
        public static bool Verify(string input, string saltBase64, string expectedHashBase64)
        {
            if (input is null) throw new ArgumentNullException(nameof(input));
            if (saltBase64 is null) throw new ArgumentNullException(nameof(saltBase64));
            if (expectedHashBase64 is null) throw new ArgumentNullException(nameof(expectedHashBase64));

            byte[] salt;
            byte[] expectedHash;

            try
            {
                salt = Convert.FromBase64String(saltBase64);
                expectedHash = Convert.FromBase64String(expectedHashBase64);
            }
            catch (FormatException)
            {
                return false; // invalid stored values
            }

            if (salt.Length != SaltSizeBytes) return false;
            if (expectedHash.Length != HashSizeBytes) return false;

            byte[] actualHash = DeriveKey(input, salt);

            return CryptographicOperations.FixedTimeEquals(actualHash, expectedHash);
        }

        private static byte[] DeriveKey(string input, byte[] salt)
        {            
            return Rfc2898DeriveBytes.Pbkdf2(input, salt, Iterations, HashAlgorithmName.SHA512, HashSizeBytes);
        }
    }
}
