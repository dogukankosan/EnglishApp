using EnglishApp.Api.Infrastructure.Errors;
using EnglishApp.Shared.Enums;

namespace EnglishApp.Api.Infrastructure.Security;

public interface ICurrentUser
{
    int Id { get; }
    bool IsAuthenticated { get; }
    bool IsAdmin { get; }
    string? ClientIp { get; }
    string? UserAgent { get; }
    string CorrelationId { get; }
}

public sealed class CurrentUser(IHttpContextAccessor accessor) : ICurrentUser
{
    private HttpContext? Ctx => accessor.HttpContext;

    public int Id => int.TryParse(Ctx?.User.FindFirst(AppClaims.UserId)?.Value, out var id)
        ? id
        : throw new AppException(ErrorCodes.Unauthorized, "Oturum bulunamadı.", StatusCodes.Status401Unauthorized);

    public bool IsAuthenticated => Ctx?.User.Identity?.IsAuthenticated == true;
    public bool IsAdmin => Ctx?.User.IsInRole(nameof(UserRole.Admin)) == true;
    public string? ClientIp => Ctx?.Connection.RemoteIpAddress?.ToString();
    public string? UserAgent => Ctx?.Request.Headers.UserAgent.ToString() is { Length: > 0 } ua ? ua[..Math.Min(ua.Length, 256)] : null;
    public string CorrelationId => Ctx?.TraceIdentifier ?? "";
}
