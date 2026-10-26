using EnglishApp.Api.Infrastructure.Errors;
using EnglishApp.Shared.Common;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace EnglishApp.Api.Infrastructure.Security;

/// <summary>Şifre değiştirmesi zorunlu kullanıcı bu işaretli uçlar dışında hiçbir şeye erişemez.</summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class)]
public sealed class AllowWhenPasswordChangeRequiredAttribute : Attribute;

public sealed class PasswordChangeRequiredFilter : IAuthorizationFilter
{
    public void OnAuthorization(AuthorizationFilterContext context)
    {
        var user = context.HttpContext.User;
        if (user.Identity?.IsAuthenticated != true) return;
        if (user.FindFirst(AppClaims.MustChangePassword)?.Value != "1") return;
        if (context.ActionDescriptor.EndpointMetadata.OfType<AllowWhenPasswordChangeRequiredAttribute>().Any()) return;

        var body = ApiResponse.Fail(ErrorCodes.PasswordChangeRequired, "Devam etmek için şifrenizi değiştirmelisiniz.");
        body.CorrelationId = context.HttpContext.TraceIdentifier;
        context.Result = new ObjectResult(body) { StatusCode = StatusCodes.Status403Forbidden };
    }
}
