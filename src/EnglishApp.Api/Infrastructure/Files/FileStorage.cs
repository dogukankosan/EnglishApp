using EnglishApp.Api.Infrastructure.Errors;

namespace EnglishApp.Api.Infrastructure.Security;

/// <summary>
/// Profil resimleri: App_Data/uploads/profiles altında, tahmin edilemez isimle.
/// Dosya tipi uzantıdan değil, içeriğin ilk byte'larından (magic number) kontrol edilir.
/// </summary>
public interface IFileStorage
{
    Task<string> SaveProfilePhotoAsync(IFormFile file, CancellationToken ct);
    void DeleteProfilePhoto(string fileName);
    string? GetProfilePhotoPath(string fileName);
}

public sealed class FileStorage(IWebHostEnvironment env, IConfiguration config, ILogger<FileStorage> logger) : IFileStorage
{
    public const long MaxPhotoBytes = 2 * 1024 * 1024;

    private string ProfileDir => Path.Combine(
        config["Storage:RootPath"] ?? Path.Combine(env.ContentRootPath, "App_Data", "uploads"), "profiles");

    public async Task<string> SaveProfilePhotoAsync(IFormFile file, CancellationToken ct)
    {
        if (file.Length == 0) throw new AppException(ErrorCodes.InvalidFile, "Dosya boş.");
        if (file.Length > MaxPhotoBytes) throw new AppException(ErrorCodes.InvalidFile, "Resim en fazla 2 MB olabilir.");

        var header = new byte[12];
        await using (var s = file.OpenReadStream())
        {
            var read = await s.ReadAtLeastAsync(header, header.Length, throwOnEndOfStream: false, ct);
            if (read < 4) throw new AppException(ErrorCodes.InvalidFile, "Geçersiz resim dosyası.");
        }

        var ext = DetectImageExtension(header)
            ?? throw new AppException(ErrorCodes.InvalidFile, "Sadece JPG, PNG veya WEBP yükleyebilirsiniz.");

        Directory.CreateDirectory(ProfileDir);
        var fileName = $"{Guid.NewGuid():N}{ext}";
        var path = Path.Combine(ProfileDir, fileName);

        try
        {
            await using var target = File.Create(path);
            await file.CopyToAsync(target, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Profil resmi kaydedilemedi: {Path}", path);
            TryDelete(path);
            throw;
        }

        return fileName;
    }

    public void DeleteProfilePhoto(string fileName)
    {
        var path = GetProfilePhotoPath(fileName);
        if (path is not null) TryDelete(path);
    }

    public string? GetProfilePhotoPath(string fileName)
    {
        // Yol enjeksiyonuna karşı: sadece dosya adı kabul edilir
        if (string.IsNullOrWhiteSpace(fileName) || fileName != Path.GetFileName(fileName)) return null;
        var path = Path.Combine(ProfileDir, fileName);
        return File.Exists(path) ? path : null;
    }

    public static string? DetectImageExtension(ReadOnlySpan<byte> h)
    {
        if (h.Length >= 3 && h[0] == 0xFF && h[1] == 0xD8 && h[2] == 0xFF) return ".jpg";
        if (h.Length >= 8 && h[0] == 0x89 && h[1] == 0x50 && h[2] == 0x4E && h[3] == 0x47) return ".png";
        if (h.Length >= 12 && h[0] == 'R' && h[1] == 'I' && h[2] == 'F' && h[3] == 'F'
            && h[8] == 'W' && h[9] == 'E' && h[10] == 'B' && h[11] == 'P') return ".webp";
        return null;
    }

    private void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) { logger.LogWarning(ex, "Dosya silinemedi: {Path}", path); }
    }
}
