using System.Collections.Concurrent;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using EnglishApp.Shared.Dtos;
using EnglishApp.Web.Infrastructure;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.Caching.Memory;

namespace EnglishApp.Web.Services;

public static class WebClaims
{
    public const string Photo = "photo";
    public const string MustChangePassword = "mcp";
}

/// <summary>
/// API token'ları tarayıcıya hiç verilmez: şifreli, httpOnly cookie içinde (AuthenticationProperties) saklanır.
/// Süresi dolmak üzere olan access token burada otomatik yenilenir.
/// </summary>
public sealed class TokenService(ApiClient api, IMemoryCache cache, ILogger<TokenService> logger)
{
    private const string AccessKey = "access_token";
    private const string RefreshKey = "refresh_token";
    private const string AccessExpKey = "access_expires_utc";
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Locks = new();

    public static async Task SignInAsync(HttpContext ctx, TokenResponse tokens, bool persistent)
    {
        var u = tokens.User;
        var identity = new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, u.Id.ToString()),
            new Claim(ClaimTypes.Name, u.FullName),
            new Claim(ClaimTypes.Email, u.Email),
            new Claim(ClaimTypes.Role, u.Role.ToString()),
            new Claim(WebClaims.Photo, u.ProfileImage ?? ""),
            new Claim(WebClaims.MustChangePassword, u.MustChangePassword ? "1" : "0")
        ], CookieAuthenticationDefaults.AuthenticationScheme);

        var props = new AuthenticationProperties
        {
            IsPersistent = persistent,
            // Cookie, refresh token ile birlikte ölür
            ExpiresUtc = TrTime.ToUtcOffset(tokens.RefreshTokenExpiresAt),
            AllowRefresh = false
        };
        props.Items["persistent"] = persistent ? "1" : "0";
        props.StoreTokens(
        [
            new AuthenticationToken { Name = AccessKey, Value = tokens.AccessToken },
            new AuthenticationToken { Name = RefreshKey, Value = tokens.RefreshToken },
            new AuthenticationToken { Name = AccessExpKey, Value = TrTime.ToUtcOffset(tokens.AccessTokenExpiresAt).ToString("O") }
        ]);

        await ctx.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity), props);
    }

    /// <summary>Geçerli bir access token döner; gerekirse yeniler. Oturum bittiyse null.</summary>
    public async Task<string?> GetAccessTokenAsync(HttpContext ctx, bool forceRefresh = false, CancellationToken ct = default)
    {
        var auth = await ctx.AuthenticateAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        if (!auth.Succeeded || auth.Properties is null) return null;

        var access = auth.Properties.GetTokenValue(AccessKey);
        var refresh = auth.Properties.GetTokenValue(RefreshKey);
        var expires = DateTimeOffset.TryParse(auth.Properties.GetTokenValue(AccessExpKey), out var e) ? e : DateTimeOffset.MinValue;

        if (!forceRefresh && access is not null && expires > DateTimeOffset.UtcNow.AddSeconds(60))
            return access;
        if (string.IsNullOrEmpty(refresh)) return null;

        var tokens = await RefreshOnceAsync(refresh, ct);
        if (tokens is null) return null;

        await SignInAsync(ctx, tokens, auth.Properties.Items.TryGetValue("persistent", out var p) && p == "1");
        return tokens.AccessToken;
    }

    /// <summary>
    /// Aynı anda gelen birden fazla AJAX isteği aynı refresh token'ı kullanırsa API bunu "çalıntı" sayar.
    /// Bu yüzden yenileme kullanıcı başına kilitli yapılır ve sonuç 30 sn cache'lenir.
    /// </summary>
    private async Task<TokenResponse?> RefreshOnceAsync(string refreshToken, CancellationToken ct)
    {
        var key = "refresh:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(refreshToken)));
        if (cache.TryGetValue(key, out TokenResponse? cached)) return cached;

        var gate = Locks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            if (cache.TryGetValue(key, out cached)) return cached;

            var result = await api.RefreshAsync(refreshToken, ct);
            if (!result.Success || result.Data is null)
            {
                logger.LogInformation("Token yenilenemedi: {ErrorCode}", result.Error?.Code);
                return null;
            }

            cache.Set(key, result.Data, TimeSpan.FromSeconds(30));
            return result.Data;
        }
        finally
        {
            gate.Release();
            Locks.TryRemove(key, out _);
        }
    }

    public static async Task<(string? Access, string? Refresh)> GetStoredTokensAsync(HttpContext ctx)
    {
        var auth = await ctx.AuthenticateAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return (auth.Properties?.GetTokenValue(AccessKey), auth.Properties?.GetTokenValue(RefreshKey));
    }
}
