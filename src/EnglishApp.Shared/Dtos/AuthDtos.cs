using EnglishApp.Shared.Enums;

namespace EnglishApp.Shared.Dtos;

public class LoginRequest
{
    public string Email { get; set; } = "";
    public string Password { get; set; } = "";
    /// <summary>Cloudflare Turnstile cevabı (3 hatalı denemeden sonra zorunlu).</summary>
    public string? CaptchaToken { get; set; }
}

/// <summary>Giriş ekranı: güvenlik doğrulaması gerekiyor mu?</summary>
public class LoginChallengeDto
{
    public bool CaptchaRequired { get; set; }
    /// <summary>Doğrulama gerekiyorsa Turnstile site anahtarı (herkese açık).</summary>
    public string? SiteKey { get; set; }
}

public class RefreshRequest
{
    public string RefreshToken { get; set; } = "";
}

public class LogoutRequest
{
    public string RefreshToken { get; set; } = "";
}

public class ChangePasswordRequest
{
    public string CurrentPassword { get; set; } = "";
    public string NewPassword { get; set; } = "";
    public string NewPasswordConfirm { get; set; } = "";
}

public class TokenResponse
{
    public string AccessToken { get; set; } = "";
    public DateTime AccessTokenExpiresAt { get; set; }
    public string RefreshToken { get; set; } = "";
    public DateTime RefreshTokenExpiresAt { get; set; }
    public UserInfoDto User { get; set; } = new();
}

public class UserInfoDto
{
    public int Id { get; set; }
    public string Email { get; set; } = "";
    public string FullName { get; set; } = "";
    public UserRole Role { get; set; }
    public string? ProfileImage { get; set; }
    public bool MustChangePassword { get; set; }
}
