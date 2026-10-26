using System.Text.Json.Serialization;
using EnglishApp.Api.Infrastructure.Settings;
using Microsoft.Extensions.Caching.Memory;

namespace EnglishApp.Api.Infrastructure.Security;

/// <summary>
/// Hatalı giriş sayacı (bellekte). Aynı IP'den veya aynı e-postaya 3 hatalı denemeden sonra
/// güvenlik doğrulaması (Turnstile) istenir. E-posta sayacı, çok IP'den tek hesaba saldırıyı da yakalar.
/// Kullanıcı var/yok ayrımı yapılmaz (e-posta tahmini zorlaşır).
/// </summary>
public interface ILoginAttemptTracker
{
    bool IsCaptchaRequired(string? ip, string? email);
    void RecordFailure(string? ip, string? email);
    void Reset(string? ip, string? email);
}

public sealed class LoginAttemptTracker(IMemoryCache cache) : ILoginAttemptTracker
{
    public const int CaptchaThreshold = 3;
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(30);

    private sealed class Counter { public int Count; }
    private readonly Lock _lock = new();

    public bool IsCaptchaRequired(string? ip, string? email) =>
        Get(IpKey(ip)) >= CaptchaThreshold || Get(EmailKey(email)) >= CaptchaThreshold;

    public void RecordFailure(string? ip, string? email)
    {
        Increment(IpKey(ip));
        Increment(EmailKey(email));
    }

    public void Reset(string? ip, string? email)
    {
        if (IpKey(ip) is { } k1) cache.Remove(k1);
        if (EmailKey(email) is { } k2) cache.Remove(k2);
    }

    private int Get(string? key) => key is not null && cache.TryGetValue<Counter>(key, out var c) ? c!.Count : 0;

    private void Increment(string? key)
    {
        if (key is null) return;
        lock (_lock)
        {
            var c = cache.Get<Counter>(key) ?? new Counter();
            c.Count++;
            // Her hatalı denemede süre baştan başlar
            cache.Set(key, c, new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = Window });
        }
    }

    private static string? IpKey(string? ip) => string.IsNullOrWhiteSpace(ip) ? null : "login-fail:ip:" + ip;
    private static string? EmailKey(string? email) =>
        string.IsNullOrWhiteSpace(email) ? null : "login-fail:mail:" + email.Trim().ToLowerInvariant();
}

/// <summary>Cloudflare Turnstile doğrulaması. Anahtarlar Admin > Ayarlar > Güvenlik'ten girilir (gizli anahtar şifreli).</summary>
public interface ITurnstileService
{
    /// <summary>Site anahtarı; ayarlanmamışsa (veya gizli anahtar yoksa) null: doğrulama kapalı demektir.</summary>
    Task<string?> GetSiteKeyAsync(CancellationToken ct);
    Task<bool> VerifyAsync(string? token, string? ip, CancellationToken ct);
}

public sealed class TurnstileService(IHttpClientFactory httpFactory, ISettingsService settings, ILogger<TurnstileService> logger) : ITurnstileService
{
    public const string HttpClientName = "Turnstile";
    private const string VerifyUrl = "https://challenges.cloudflare.com/turnstile/v0/siteverify";

    private sealed class VerifyResponse
    {
        public bool Success { get; set; }
        [JsonPropertyName("error-codes")] public string[]? ErrorCodes { get; set; }
        public string? Hostname { get; set; }
    }

    public async Task<string?> GetSiteKeyAsync(CancellationToken ct)
    {
        var site = await settings.GetAsync(SettingKeys.TurnstileSiteKey, ct);
        var secret = await settings.GetAsync(SettingKeys.TurnstileSecretKey, ct);
        return string.IsNullOrWhiteSpace(site) || string.IsNullOrWhiteSpace(secret) ? null : site.Trim();
    }

    public async Task<bool> VerifyAsync(string? token, string? ip, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 2048) return false;
        var secret = await settings.GetAsync(SettingKeys.TurnstileSecretKey, ct);
        if (string.IsNullOrWhiteSpace(secret)) return false;

        try
        {
            var form = new Dictionary<string, string> { ["secret"] = secret.Trim(), ["response"] = token };
            if (!string.IsNullOrWhiteSpace(ip)) form["remoteip"] = ip;

            using var res = await httpFactory.CreateClient(HttpClientName).PostAsync(VerifyUrl, new FormUrlEncodedContent(form), ct);
            var body = await res.Content.ReadFromJsonAsync<VerifyResponse>(cancellationToken: ct);
            if (body?.Success == true) return true;

            logger.LogWarning("Turnstile doğrulaması başarısız: {Codes}", string.Join(",", body?.ErrorCodes ?? ["no-body"]));
            return false;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            // Cloudflare'e ulaşılamazsa güvenli tarafta kalınır (doğrulama geçmez)
            logger.LogError(ex, "Turnstile servisine ulaşılamadı.");
            return false;
        }
    }
}
