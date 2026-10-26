using EnglishApp.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;

namespace EnglishApp.Web.Infrastructure;

/// <summary>Şifresini değiştirmesi gereken kullanıcıyı (ilk giriş, admin sıfırlaması) şifre ekranına yönlendirir.</summary>
public sealed class PasswordChangeRedirectFilter : IActionFilter
{
    public void OnActionExecuting(ActionExecutingContext context)
    {
        var user = context.HttpContext.User;
        if (user.Identity?.IsAuthenticated != true || user.FindFirst(WebClaims.MustChangePassword)?.Value != "1") return;
        if (context.ActionDescriptor is not ControllerActionDescriptor action) return;
        if (action.ControllerName is "Account" or "Bff") return;

        context.Result = new RedirectToActionResult("ChangePassword", "Account", new { area = "" });
    }

    public void OnActionExecuted(ActionExecutedContext context) { }
}
