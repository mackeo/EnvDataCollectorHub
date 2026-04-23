using System;
using System.Security.Cryptography;
using System.Text;

namespace EnvDataCollector.Services
{
    public static class CryptoHelper
    {
        private static readonly byte[] Key = Encoding.UTF8.GetBytes("EnvColl@Key12345");
        private static readonly byte[] IV  = Encoding.UTF8.GetBytes("EnvColl@IV123456");

        public static string Encrypt(string plain)
        {
            if (string.IsNullOrEmpty(plain)) return plain;
            try
            {
                using var aes = Aes.Create();
                aes.Key = Key; aes.IV = IV;
                var bytes = Encoding.UTF8.GetBytes(plain);
                return Convert.ToBase64String(
                    aes.CreateEncryptor().TransformFinalBlock(bytes, 0, bytes.Length));
            }
            catch { return plain; }
        }

        public static string Decrypt(string cipher)
        {
            if (string.IsNullOrEmpty(cipher)) return cipher;
            try
            {
                using var aes = Aes.Create();
                aes.Key = Key; aes.IV = IV;
                var bytes = Convert.FromBase64String(cipher);
                return Encoding.UTF8.GetString(
                    aes.CreateDecryptor().TransformFinalBlock(bytes, 0, bytes.Length));
            }
            catch { return cipher; }
        }
    }
}
