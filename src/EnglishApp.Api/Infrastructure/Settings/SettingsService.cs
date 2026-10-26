using Dapper;
using EnglishApp.Api.Infrastructure.Data;
using EnglishApp.Api.Infrastructure.Security;
using Microsoft.Extensions.Caching.Memory;

namespace EnglishApp.Api.Infrastructure.Settings;

public sealed class SettingRow
{
    public string Key { get; set; } = "";
    public string? Value { get; set; }
    public bool IsEncrypted { get; set; }
    public string GroupName { get; set; } = "";
    public string? DescriptionTr { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public int? UpdatedBy { get; set; }
}

/// <summary>
/// app.Settings tablosundaki dinamik ayarlar. Bellekte 5 dk cache'lenir,
/// güncellemede cache temizlenir. Şifreli değerler okunurken çözülür.
/// </summary>
public interface ISettingsService
{
    Task<string?> GetAsync(string key, CancellationToken ct = default);
    Task<int> GetIntAsync(string key, int defaultValue, CancellationToken ct = default);
    Task<bool> GetBoolAsync(string key, bool defaultValue, CancellationToken ct = default);
    Task<IReadOnlyList<SettingRow>> GetAllRawAsync(CancellationToken ct = default);
    Task SetAsync(string key, string? value, int updatedBy, CancellationToken ct = default);
}

public sealed class SettingsService(
    IDbConnectionFactory db,
    IEncryptionService encryption,
    IMemoryCache cache,
    Time.ITrClock clock,
    ILogger<SettingsService> logger) : ISettingsService
{
    private const string CacheKey = "app-settings";

    public async Task<string?> GetAsync(string key, CancellationToken ct = default)
    {
        var all = await LoadAsync(ct);
        if (!all.TryGetValue(key, out var row) || string.IsNullOrEmpty(row.Value)) return null;
        if (!row.IsEncrypted) return row.Value;

        try
        {
            return encryption.Decrypt(row.Value);
        }
        catch (Exception ex)
        {
            // Anahtar değiştiyse veya değer bozulduysa uygulama çökmesin, loglansın
            logger.LogError(ex, "Şifreli ayar çözülemedi: {SettingKey}", key);
            return null;
        }
    }

    public async Task<int> GetIntAsync(string key, int defaultValue, CancellationToken ct = default) =>
        int.TryParse(await GetAsync(key, ct), System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : defaultValue;

    public async Task<bool> GetBoolAsync(string key, bool defaultValue, CancellationToken ct = default) =>
        bool.TryParse(await GetAsync(key, ct), out var v) ? v : defaultValue;

    public async Task<IReadOnlyList<SettingRow>> GetAllRawAsync(CancellationToken ct = default) =>
        (await LoadAsync(ct)).Values.OrderBy(s => s.GroupName).ThenBy(s => s.Key).ToList();

    public async Task SetAsync(string key, string? value, int updatedBy, CancellationToken ct = default)
    {
        var all = await LoadAsync(ct);
        if (!all.TryGetValue(key, out var row))
            throw new Errors.AppException(Errors.ErrorCodes.SettingNotFound, $"Ayar bulunamadı: {key}", StatusCodes.Status404NotFound);

        var stored = string.IsNullOrEmpty(value) ? null : row.IsEncrypted ? encryption.Encrypt(value) : value.Trim();

        await using var conn = await db.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE app.Settings SET Value = @Value, UpdatedAt = @Now, UpdatedBy = @UpdatedBy WHERE [Key] = @Key",
            new { Key = key, Value = stored, Now = clock.Now, UpdatedBy = updatedBy }, cancellationToken: ct));

        cache.Remove(CacheKey);
    }

    private async Task<Dictionary<string, SettingRow>> LoadAsync(CancellationToken ct)
    {
        if (cache.TryGetValue(CacheKey, out Dictionary<string, SettingRow>? cached) && cached is not null)
            return cached;

        await using var conn = await db.OpenAsync(ct);
        var rows = await conn.QueryAsync<SettingRow>(new CommandDefinition(
            "SELECT [Key], Value, IsEncrypted, GroupName, DescriptionTr, UpdatedAt, UpdatedBy FROM app.Settings",
            cancellationToken: ct));

        var dict = rows.ToDictionary(r => r.Key, StringComparer.OrdinalIgnoreCase);
        cache.Set(CacheKey, dict, TimeSpan.FromMinutes(5));
        return dict;
    }
}

public static class SettingKeys
{
    public const string SmtpHost = "Smtp.Host";
    public const string SmtpPort = "Smtp.Port";
    public const string SmtpUseSsl = "Smtp.UseSsl";
    public const string SmtpUserName = "Smtp.UserName";
    public const string SmtpPassword = "Smtp.Password";
    public const string SmtpFromAddress = "Smtp.FromAddress";
    public const string SmtpFromName = "Smtp.FromName";
    public const string SmtpSignatureHtml = "Smtp.SignatureHtml";
    public const string JobsSecretKey = "Jobs.SecretKey";
    public const string TurnstileSiteKey = "Security.Turnstile.SiteKey";
    public const string TurnstileSecretKey = "Security.Turnstile.SecretKey";
    public const string AppUrl = "General.AppUrl";
    public const string DefaultDailyTokenLimit = "General.DefaultDailyTokenLimit";
}
