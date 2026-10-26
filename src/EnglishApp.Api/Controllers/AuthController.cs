using EnglishApp.Api.Infrastructure.Security;
using EnglishApp.Api.Services;
using EnglishApp.Shared.Common;
using EnglishApp.Shared.Dtos;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace EnglishApp.Api.Controllers;

[Route("api/v1/auth")]
public sealed class AuthController(IAuthService auth) : ApiControllerBase
{
    [HttpPost("login")]
    [AllowAnonymous]
    [EnableRateLimiting("auth")]
    public async Task<ActionResult<ApiResponse<TokenResponse>>> Login(LoginRequest req, CancellationToken ct) =>
        Success(await auth.LoginAsync(req, ct));

    /// <summary>Giriş ekranı açılırken: bu IP / e-posta için güvenlik doğrulaması gerekiyor mu?</summary>
    [HttpGet("login-challenge")]
    [AllowAnonymous]
    public async Task<ActionResult<ApiResponse<LoginChallengeDto>>> LoginChallenge([FromQuery] string? email, CancellationToken ct) =>
        Success(await auth.GetLoginChallengeAsync(email?.Length > 256 ? null : email, ct));

    [HttpPost("refresh")]
    [AllowAnonymous]
    [EnableRateLimiting("auth")]
    public async Task<ActionResult<ApiResponse<TokenResponse>>> Refresh(RefreshRequest req, CancellationToken ct) =>
        Success(await auth.RefreshAsync(req.RefreshToken, ct));

    [HttpPost("logout")]
    [AllowWhenPasswordChangeRequired]
    public async Task<ActionResult<ApiResponse>> Logout(LogoutRequest req, CancellationToken ct)
    {
        await auth.LogoutAsync(req.RefreshToken, ct);
        return Success();
    }

    [HttpPost("change-password")]
    [AllowWhenPasswordChangeRequired]
    [EnableRateLimiting("auth")]
    public async Task<ActionResult<ApiResponse<TokenResponse>>> ChangePassword(ChangePasswordRequest req, CancellationToken ct) =>
        Success(await auth.ChangePasswordAsync(req, ct));

    [HttpGet("me")]
    [AllowWhenPasswordChangeRequired]
    public async Task<ActionResult<ApiResponse<UserInfoDto>>> Me(CancellationToken ct) =>
        Success(await auth.GetMeAsync(ct));
}
