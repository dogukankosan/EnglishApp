using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using EnglishApp.Api.Infrastructure.Time;
using EnglishApp.Shared.Enums;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace EnglishApp.Api.Infrastructure.Security;

public static class AppClaims
{
    public const string UserId = "sub";
    public const string Name = "name";
    public const string Email = "email";
    public const string Role = "role";
    public const string SecurityStamp = "sst";
    public const string MustChangePassword = "mcp";
}

public sealed class JwtOptions
{
    public string Issuer { get; set; } = "EnglishApp.Api";
    public string Audience { get; set; } = "EnglishApp";
    /// <summary>En az 32 karakter.</summary>
    public string Key { get; set; } = "";
    public int AccessTokenMinutes { get; set; } = 15;
    public int RefreshTokenDays { get; set; } = 7;

    public SymmetricSecurityKey SigningKey => new(Encoding.UTF8.GetBytes(Key));
}

public record AccessToken(string Token, DateTime ExpiresAtTr);
public record NewRefreshToken(string Token, byte[] Hash, DateTime ExpiresAtTr);

public interface IJwtTokenService
{
    AccessToken CreateAccessToken(int userId, string email, string fullName, UserRole role, Guid securityStamp, bool mustChangePassword);
    NewRefreshToken CreateRefreshToken();
    byte[] HashRefreshToken(string token);
}

public sealed class JwtTokenService(JwtOptions options, ITrClock clock) : IJwtTokenService
{
    private readonly JsonWebTokenHandler _handler = new();

    public AccessToken CreateAccessToken(int userId, string email, string fullName, UserRole role, Guid securityStamp, bool mustChangePassword)
    {
        var utcNow = DateTime.UtcNow;
        var expiresUtc = utcNow.AddMinutes(options.AccessTokenMinutes);

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = options.Issuer,
            Audience = options.Audience,
            IssuedAt = utcNow,
            NotBefore = utcNow,
            Expires = expiresUtc,
            SigningCredentials = new SigningCredentials(options.SigningKey, SecurityAlgorithms.HmacSha256),
            Subject = new ClaimsIdentity(
            [
                new Claim(AppClaims.UserId, userId.ToString()),
                new Claim(AppClaims.Email, email),
                new Claim(AppClaims.Name, fullName),
                new Claim(AppClaims.Role, role.ToString()),
                new Claim(AppClaims.SecurityStamp, securityStamp.ToString("N")),
                new Claim(AppClaims.MustChangePassword, mustChangePassword ? "1" : "0"),
                new Claim("jti", Guid.NewGuid().ToString("N"))
            ])
        };

        return new AccessToken(_handler.CreateToken(descriptor), clock.ToTr(expiresUtc));
    }

    public NewRefreshToken CreateRefreshToken()
    {
        var token = Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(64));
        return new NewRefreshToken(token, HashRefreshToken(token), clock.Now.AddDays(options.RefreshTokenDays));
    }

    public byte[] HashRefreshToken(string token) => SHA256.HashData(Encoding.UTF8.GetBytes(token));
}
