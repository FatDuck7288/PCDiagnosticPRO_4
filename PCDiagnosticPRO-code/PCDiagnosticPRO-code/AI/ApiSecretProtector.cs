using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace PCDiagnosticPro.AI
{
    /// <summary>
    /// Protects API secrets at rest. Uses DPAPI CurrentUser on Windows,
    /// with a local encrypted fallback when DPAPI is unavailable.
    /// </summary>
    public sealed class ApiSecretProtector
    {
        private const string DpapiPrefix = "dpapi:";
        private const string FallbackPrefix = "fallback:";
        private static readonly byte[] FallbackSalt = Encoding.UTF8.GetBytes("PCXray.ApiSecret.v1");

        public string Protect(string plaintext, out bool usedFallback)
        {
            usedFallback = false;
            if (string.IsNullOrWhiteSpace(plaintext))
            {
                return string.Empty;
            }

            var raw = Encoding.UTF8.GetBytes(plaintext);
            try
            {
                var cipher = ProtectedData.Protect(raw, null, DataProtectionScope.CurrentUser);
                return DpapiPrefix + Convert.ToBase64String(cipher);
            }
            catch (Exception ex)
            {
                App.LogMessage($"[AI][ApiSecretProtector] DPAPI protect failed, using fallback: {ex.Message}");
                usedFallback = true;
                return FallbackPrefix + Convert.ToBase64String(EncryptFallback(raw));
            }
        }

        public string Unprotect(string? protectedValue)
        {
            if (string.IsNullOrWhiteSpace(protectedValue))
            {
                return string.Empty;
            }

            var value = protectedValue.Trim();
            try
            {
                if (value.StartsWith(DpapiPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    var blob = Convert.FromBase64String(value[DpapiPrefix.Length..]);
                    var plain = ProtectedData.Unprotect(blob, null, DataProtectionScope.CurrentUser);
                    return Encoding.UTF8.GetString(plain);
                }

                if (value.StartsWith(FallbackPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    var blob = Convert.FromBase64String(value[FallbackPrefix.Length..]);
                    var plain = DecryptFallback(blob);
                    return Encoding.UTF8.GetString(plain);
                }

                // Backward compatibility: if value does not include a known prefix,
                // treat it as plaintext but do not log the content.
                return value;
            }
            catch (Exception ex)
            {
                App.LogMessage($"[AI][ApiSecretProtector] Unprotect failed: {ex.Message}");
                return string.Empty;
            }
        }

        private static byte[] EncryptFallback(byte[] plaintext)
        {
            using var aes = Aes.Create();
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;
            aes.Key = DeriveFallbackKey();
            aes.GenerateIV();

            using var ms = new MemoryStream();
            ms.Write(aes.IV, 0, aes.IV.Length);

            using (var crypto = new CryptoStream(ms, aes.CreateEncryptor(), CryptoStreamMode.Write, leaveOpen: true))
            {
                crypto.Write(plaintext, 0, plaintext.Length);
                crypto.FlushFinalBlock();
            }

            return ms.ToArray();
        }

        private static byte[] DecryptFallback(byte[] encrypted)
        {
            if (encrypted.Length < 16)
            {
                throw new InvalidOperationException("Fallback secret payload is invalid.");
            }

            using var aes = Aes.Create();
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;
            aes.Key = DeriveFallbackKey();

            var iv = new byte[16];
            Buffer.BlockCopy(encrypted, 0, iv, 0, 16);
            aes.IV = iv;

            using var input = new MemoryStream(encrypted, 16, encrypted.Length - 16);
            using var crypto = new CryptoStream(input, aes.CreateDecryptor(), CryptoStreamMode.Read);
            using var output = new MemoryStream();
            crypto.CopyTo(output);
            return output.ToArray();
        }

        private static byte[] DeriveFallbackKey()
        {
            var seed = $"{Environment.UserName}|{Environment.MachineName}|PCXray";
            using var derive = new Rfc2898DeriveBytes(seed, FallbackSalt, 100_000, HashAlgorithmName.SHA256);
            return derive.GetBytes(32);
        }
    }
}
