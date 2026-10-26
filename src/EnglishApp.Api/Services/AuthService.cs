using EnglishApp.Api.Data;
using EnglishApp.Api.Infrastructure.Errors;
using EnglishApp.Api.Infrastructure.Logging;
using EnglishApp.Api.Infrastructure.Security;
using EnglishApp.Api.Infrastructure.Time;
using EnglishApp.Api.Infrastructure.Validation;
using EnglishApp.Shared.Dtos;
using EnglishApp.Shared.Enums;
using FluentValidation;
using Microsoft.Extensions.Caching.Memory;

namespace EnglishApp.Api.Services;

public interface IAuthService
{
    Task<TokenResponse> LoginAsync(LoginRequest req, CancellationToken ct);
    Task<TokenResponse> RefreshAsync(string refreshToken, CancellationToken ct);
    Task LogoutAsync(string refreshToken, CancellationToken ct);
    Task<TokenResponse> ChangePasswordAsync(ChangePasswordRequest req, CancellationToken ct);
    Task<UserInfoDto> GetMeAsync(CancellationToken ct);
    Task<LoginChallengeDto> GetLoginChallengeAsync(string? email, CancellationToken ct);
}

public sealed class AuthService(
    IUserRepository users,
    IRefreshTokenRepository refreshTokens,
    IPasswordService passwords,
    IJwtTokenService jwt,
    IAuthStateCache authCache,
    ICurrentUser currentUser,
    ITrClock clock,
    ILoginAttemptTracker attempts,
    ITurnstileService turnstile,
    ILogger<AuthService> logger) : IAuthService
{
    public const int MaxFailedAttempts = 5;
    public static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(15);

    // Kullanıcı yokken de aynı sürede yanıt vermek için (e-posta tahmin etmeyi zorlaştırır)
    private static readonly string DummyHash = new PasswordService().Hash("dummy-password-1");

    public async Task<TokenResponse> LoginAsync(LoginRequest req, CancellationToken ct)
    {
        var email = NormalizeEmail(req.Email);
        var ip = currentUser.ClientIp;

        // 3 hatalı denemeden sonra (IP veya e-posta bazında) güvenlik doğrulaması zorunlu
        if (attempts.IsCaptchaRequired(ip, email) && await turnstile.GetSiteKeyAsync(ct) is not null)
        {
            if (string.IsNullOrWhiteSpace(req.CaptchaToken))
                throw new AppException(ErrorCodes.CaptchaRequired, "Güvenlik doğrulamasını tamamlayın.", StatusCodes.Status400BadRequest);
            if (!await turnstile.VerifyAsync(req.CaptchaToken, ip, ct))
            {
                logger.LogWarning("Güvenlik doğrulaması geçilemedi ({Email})", LogSanitizer.MaskEmail(email));
                throw new AppException(ErrorCodes.CaptchaInvalid, "Güvenlik doğrulaması geçilemedi. Lütfen tekrar deneyin.", StatusCodes.Status400BadRequest);
            }
        }

        var user = await users.GetByEmailAsync(email, ct);
        var now = clock.Now;

        if (user is null)
        {
            passwords.Verify(DummyHash, req.Password);
            attempts.RecordFailure(ip, email);
            logger.LogWarning("Başarısız giriş: kullanıcı bulunamadı ({Email})", LogSanitizer.MaskEmail(email));
            throw InvalidCredentials();
        }

        if (user.LockoutEndAt > now)
        {
            var minutes = (int)Math.Ceiling((user.LockoutEndAt.Value - now).TotalMinutes);
            logger.LogWarning("Kilitli hesaba giriş denemesi. UserId: {TargetUserId}", user.Id);
            attempts.RecordFailure(ip, email);
            throw new AppException(ErrorCodes.AccountLocked,
                $"Çok fazla hatalı deneme. Hesabınız {minutes} dakika kilitli.", StatusCodes.Status423Locked);
        }

        var check = passwords.Verify(user.PasswordHash, req.Password);
        if (check == PasswordCheck.Failed)
        {
            var failed = (byte)(user.FailedLoginCount + 1);
            DateTime? lockoutEnd = null;
            if (failed >= MaxFailedAttempts)
            {
                lockoutEnd = now.Add(LockoutDuration);
                failed = 0;
                logger.LogWarning("Hesap kilitlendi ({Max} hatalı deneme). UserId: {TargetUserId}", MaxFailedAttempts, user.Id);
            }
            else
            {
                logger.LogWarning("Başarısız giriş: hatalı şifre. UserId: {TargetUserId}, Deneme: {Attempt}", user.Id, failed);
            }
            await users.RecordLoginFailureAsync(user.Id, failed, lockoutEnd, ct);
            attempts.RecordFailure(ip, email);
            throw InvalidCredentials();
        }

        // Şifre doğru olduktan SONRA pasiflik bilgisi verilir
        if (!user.IsActive)
        {
            logger.LogWarning("Pasif kullanıcı giriş denedi. UserId: {TargetUserId}", user.Id);
            throw new AppException(ErrorCodes.AccountInactive, "Hesabınız pasif durumda. Yönetici ile iletişime geçin.", StatusCodes.Status403Forbidden);
        }

        var newHash = check == PasswordCheck.SuccessNeedsRehash ? passwords.Hash(req.Password) : null;
        await users.RecordLoginSuccessAsync(user.Id, now, newHash, ct);
        attempts.Reset(ip, email);

        logger.LogInformation("Giriş başarılı. UserId: {TargetUserId}", user.Id);
        return await IssueTokensAsync(user, Guid.NewGuid(), ct);
    }

    public async Task<LoginChallengeDto> GetLoginChallengeAsync(string? email, CancellationToken ct)
    {
        var mail = string.IsNullOrWhiteSpace(email) ? null : NormalizeEmail(email);
        if (!attempts.IsCaptchaRequired(currentUser.ClientIp, mail)) return new LoginChallengeDto();
        var siteKey = await turnstile.GetSiteKeyAsync(ct);
        return new LoginChallengeDto { CaptchaRequired = siteKey is not null, SiteKey = siteKey };
    }

    public async Task<TokenResponse> RefreshAsync(string refreshToken, CancellationToken ct)
    {
        var now = clock.Now;
        var row = await refreshTokens.GetByHashAsync(jwt.HashRefreshToken(refreshToken), ct);
        if (row is null) throw InvalidRefresh();

        if (row.RevokedAt is not null)
        {
            if (row.RevokeReason == RevokeReason.Rotated)
            {
                // Daha önce kullanılmış token tekrar geldi → çalınmış olabilir, tüm zinciri kapat
                await refreshTokens.RevokeFamilyAsync(row.FamilyId, RevokeReason.ReuseDetected, now, ct);
                logger.LogWarning("Refresh token tekrar kullanımı tespit edildi, oturum zinciri kapatıldı. UserId: {TargetUserId}", row.UserId);
            }
            throw InvalidRefresh();
        }

        if (row.ExpiresAt <= now) throw InvalidRefresh();

        var user = await users.GetByIdAsync(row.UserId, ct);
        if (user is null || !user.IsActive)
        {
            await refreshTokens.RevokeFamilyAsync(row.FamilyId, RevokeReason.AdminAction, now, ct);
            throw InvalidRefresh();
        }

        var response = await IssueTokensAsync(user, row.FamilyId, ct, rotateFromId: row.Id);
        return response;
    }

    public async Task LogoutAsync(string refreshToken, CancellationToken ct)
    {
        var row = await refreshTokens.GetByHashAsync(jwt.HashRefreshToken(refreshToken), ct);
        if (row is not null && row.UserId == currentUser.Id)
        {
            await refreshTokens.RevokeFamilyAsync(row.FamilyId, RevokeReason.Logout, clock.Now, ct);
            logger.LogInformation("Çıkış yapıldı. UserId: {TargetUserId}", row.UserId);
        }
    }

    public async Task<TokenResponse> ChangePasswordAsync(ChangePasswordRequest req, CancellationToken ct)
    {
        var userId = currentUser.Id;
        var user = await users.GetByIdAsync(userId, ct) ?? throw AppException.NotFound("Kullanıcı bulunamadı.");

        if (passwords.Verify(user.PasswordHash, req.CurrentPassword) == PasswordCheck.Failed)
            throw new AppException(ErrorCodes.WrongPassword, "Mevcut şifre hatalı.");
        if (req.CurrentPassword == req.NewPassword)
            throw new AppException(ErrorCodes.ValidationError, "Yeni şifre mevcut şifreden farklı olmalı.");

        var now = clock.Now;
        await users.UpdatePasswordAsync(userId, passwords.Hash(req.NewPassword), mustChange: false, userId, now, ct);
        await refreshTokens.RevokeAllForUserAsync(userId, RevokeReason.PasswordChanged, now, ct);
        authCache.Invalidate(userId);

        logger.LogInformation("Kullanıcı şifresini değiştirdi. UserId: {TargetUserId}", userId);

        // Diğer cihazlardaki oturumlar kapandı, bu cihaz için yeni token
        var updated = await users.GetByIdAsync(userId, ct) ?? throw AppException.NotFound();
        return await IssueTokensAsync(updated, Guid.NewGuid(), ct);
    }

    public async Task<UserInfoDto> GetMeAsync(CancellationToken ct)
    {
        var user = await users.GetByIdAsync(currentUser.Id, ct) ?? throw AppException.NotFound("Kullanıcı bulunamadı.");
        return ToInfo(user);
    }

    private async Task<TokenResponse> IssueTokensAsync(UserRow user, Guid familyId, CancellationToken ct, long? rotateFromId = null)
    {
        var now = clock.Now;
        var access = jwt.CreateAccessToken(user.Id, user.Email, user.FullName, user.RoleId, user.SecurityStamp, user.MustChangePassword);
        var refresh = jwt.CreateRefreshToken();

        var newId = await refreshTokens.InsertAsync(user.Id, refresh.Hash, familyId, refresh.ExpiresAtTr, now,
            currentUser.ClientIp, currentUser.UserAgent, ct);

        if (rotateFromId is not null && !await refreshTokens.RotateAsync(rotateFromId.Value, newId, now, ct))
        {
            // Aynı token ile eşzamanlı iki yenileme: ikincisini reddet
            await refreshTokens.RevokeAsync(newId, RevokeReason.ReuseDetected, now, ct);
            throw InvalidRefresh();
        }

        return new TokenResponse
        {
            AccessToken = access.Token,
            AccessTokenExpiresAt = access.ExpiresAtTr,
            RefreshToken = refresh.Token,
            RefreshTokenExpiresAt = refresh.ExpiresAtTr,
            User = ToInfo(user)
        };
    }

    public static UserInfoDto ToInfo(UserRow u) => new()
    {
        Id = u.Id,
        Email = u.Email,
        FullName = u.FullName,
        Role = u.RoleId,
        ProfileImage = u.ProfileImagePath,
        MustChangePassword = u.MustChangePassword
    };

    public static string NormalizeEmail(string email) => (email ?? "").Trim().ToLowerInvariant();

    private static AppException InvalidCredentials() =>
        new(ErrorCodes.InvalidCredentials, "E-posta veya şifre hatalı.", StatusCodes.Status401Unauthorized);

    private static AppException InvalidRefresh() =>
        new(ErrorCodes.InvalidRefreshToken, "Oturumunuzun süresi doldu. Lütfen tekrar giriş yapın.", StatusCodes.Status401Unauthorized);
}

/// <summary>
/// Her istekte token'daki SecurityStamp ve aktiflik DB ile karşılaştırılır.
/// DB'yi yormamak için sonuç 1 dk cache'lenir, değişiklikte cache silinir.
/// </summary>
public interface IAuthStateCache
{
    Task<UserAuthState?> GetAsync(int userId, CancellationToken ct);
    void Invalidate(int userId);
}

public sealed class AuthStateCache(IMemoryCache cache, IUserRepository users) : IAuthStateCache
{
    public async Task<UserAuthState?> GetAsync(int userId, CancellationToken ct)
    {
        var key = $"auth-state:{userId}";
        if (cache.TryGetValue(key, out UserAuthState? state)) return state;

        state = await users.GetAuthStateAsync(userId, ct);
        cache.Set(key, state, TimeSpan.FromMinutes(1));
        return state;
    }

    public void Invalidate(int userId) => cache.Remove($"auth-state:{userId}");
}

// ---------- Doğrulayıcılar ----------

public sealed class LoginRequestValidator : AbstractValidator<LoginRequest>
{
    public LoginRequestValidator()
    {
        RuleFor(x => x.Email).NotEmpty().WithMessage("E-posta boş olamaz.").MaximumLength(256);
        RuleFor(x => x.Password).NotEmpty().WithMessage("Şifre boş olamaz.").MaximumLength(100);
    }
}

public sealed class RefreshRequestValidator : AbstractValidator<RefreshRequest>
{
    public RefreshRequestValidator() =>
        RuleFor(x => x.RefreshToken).NotEmpty().WithMessage("Refresh token boş olamaz.").MaximumLength(200);
}

public sealed class ChangePasswordRequestValidator : AbstractValidator<ChangePasswordRequest>
{
    public ChangePasswordRequestValidator()
    {
        RuleFor(x => x.CurrentPassword).NotEmpty().WithMessage("Mevcut şifre boş olamaz.");
        RuleFor(x => x.NewPassword).StrongPassword();
        RuleFor(x => x.NewPasswordConfirm).Equal(x => x.NewPassword).WithMessage("Yeni şifreler eşleşmiyor.");
    }
}
