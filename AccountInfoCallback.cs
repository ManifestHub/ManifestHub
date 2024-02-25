using System.Security.Cryptography;

namespace ManifestHub;

public class AccountInfoCallback(
    string accountName,
    string? accountPassword = null,
    string? refreshToken = null,
    DateTime? lastRefresh = null,
    string? index = null,
    bool? aesEncrypted = null) {
    public string AccountName { get; set; } = accountName;
    public string? AccountPassword { get; set; } = accountPassword;
    public string? RefreshToken { get; set; } = refreshToken;
    public DateTime? LastRefresh { get; set; } = lastRefresh;
    public string? Index { get; set; } = index;
    public bool? AesEncrypted { get; set; } = aesEncrypted;

    // Encrypt data
    public void Encrypt(string key) {
        if (AesEncrypted is true) return;
        AccountPassword = EncryptString(AccountPassword, key);
        RefreshToken = EncryptString(RefreshToken, key);
        AesEncrypted = true;
    }

    // Decrypt data
    public void Decrypt(string key) {
        AesEncrypted ??= false;
        if (AesEncrypted is false) return;
        AccountPassword = DecryptString(AccountPassword, key);
        RefreshToken = DecryptString(RefreshToken, key);
        AesEncrypted = false;
    }

    // Helper method to encrypt a string
    private static string? EncryptString(string? text, string key) {
        if (string.IsNullOrEmpty(text)) return text;

        using var aesAlg = Aes.Create();
        aesAlg.Key = Convert.FromBase64String(key); // Decode key from Base64 to byte[]
        aesAlg.IV = new byte[16]; // Initialize to zeros or use a unique IV for each encryption
        var encryptor = aesAlg.CreateEncryptor(aesAlg.Key, aesAlg.IV);

        using var msEncrypt = new MemoryStream();
        using var csEncrypt = new CryptoStream(msEncrypt, encryptor, CryptoStreamMode.Write);
        using var swEncrypt = new StreamWriter(csEncrypt);
        swEncrypt.Write(text);

        return Convert.ToBase64String(msEncrypt.ToArray());
    }

    // Helper method to decrypt a string
    private static string? DecryptString(string? cipherText, string key) {
        if (string.IsNullOrEmpty(cipherText)) return cipherText;

        using var aesAlg = Aes.Create();
        aesAlg.Key = Convert.FromBase64String(key); // Decode key from Base64 to byte[]
        aesAlg.IV = new byte[16]; // The IV must be the same as used during encryption
        var decryptor = aesAlg.CreateDecryptor(aesAlg.Key, aesAlg.IV);

        using var msDecrypt = new MemoryStream(Convert.FromBase64String(cipherText));
        using var csDecrypt = new CryptoStream(msDecrypt, decryptor, CryptoStreamMode.Read);
        using var srDecrypt = new StreamReader(csDecrypt);
        return srDecrypt.ReadToEnd();
    }
}

