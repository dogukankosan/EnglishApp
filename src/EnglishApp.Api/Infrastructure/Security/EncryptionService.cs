using System.Security.Cryptography;
using System.Text;

namespace EnglishApp.Api.Infrastructure.Security;

/// <summary>Hassas ayarlar (API anahtarı, SMTP şifresi) için AES-256-GCM şifreleme.</summary>
public interface IEncryptionService
{
    string Encrypt(string plainText);
    string Decrypt(string cipherText);
}

public sealed class EncryptionService : IEncryptionService
{
    private const int NonceSize = 12; // AesGcm.NonceByteSizes.MaxSize
    private const int TagSize = 16;   // AesGcm.TagByteSizes.MaxSize
    private readonly byte[] _key;

    public EncryptionService(IConfiguration configuration)
        : this(configuration["Encryption:Key"]
               ?? throw new InvalidOperationException("appsettings.json içinde 'Encryption:Key' tanımlı değil."))
    {
    }

    public EncryptionService(string base64Key)
    {
        _key = Convert.FromBase64String(base64Key);
        if (_key.Length != 32)
            throw new InvalidOperationException("Encryption:Key 32 byte (256 bit) olmalı, base64 formatında.");
    }

    /// <summary>Çıktı formatı: base64( nonce[12] | tag[16] | şifreli veri )</summary>
    public string Encrypt(string plainText)
    {
        var plain = Encoding.UTF8.GetBytes(plainText);
        var output = new byte[NonceSize + TagSize + plain.Length];
        var nonce = output.AsSpan(0, NonceSize);
        var tag = output.AsSpan(NonceSize, TagSize);
        var cipher = output.AsSpan(NonceSize + TagSize);

        RandomNumberGenerator.Fill(nonce);
        using var aes = new AesGcm(_key, TagSize);
        aes.Encrypt(nonce, plain, cipher, tag);
        return Convert.ToBase64String(output);
    }

    public string Decrypt(string cipherText)
    {
        var input = Convert.FromBase64String(cipherText);
        if (input.Length < NonceSize + TagSize)
            throw new CryptographicException("Şifreli veri geçersiz.");

        var nonce = input.AsSpan(0, NonceSize);
        var tag = input.AsSpan(NonceSize, TagSize);
        var cipher = input.AsSpan(NonceSize + TagSize);
        var plain = new byte[cipher.Length];

        using var aes = new AesGcm(_key, TagSize);
        aes.Decrypt(nonce, cipher, tag, plain);
        return Encoding.UTF8.GetString(plain);
    }

    public static string GenerateKey() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
}
