using System.ComponentModel.DataAnnotations;
using EnglishApp.Shared.Dtos;
using EnglishApp.Web.Infrastructure;
using EnglishApp.Web.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace EnglishApp.Web.Controllers;

public sealed class LoginViewModel
{
    [Required(ErrorMessage = "E-posta boş olamaz.")]
    [EmailAddress(ErrorMessage = "Geçerli bir e-posta girin.")]
    public string Email { get; set; } = "";

    [Required(ErrorMessage = "Şifre boş olamaz.")]
    public string Password { get; set; } = "";

    public bool RememberMe { get; set; } = true;
    public string? ReturnUrl { get; set; }

    /// <summary>Doluysa giriş ekranında Cloudflare Turnstile doğrulaması gösterilir (formdan bağlanmaz).</summary>
    [Microsoft.AspNetCore.Mvc.ModelBinding.BindNever]
    public string? CaptchaSiteKey { get; set; }
}

public sealed class ChangePasswordViewModel
{
    [Required(ErrorMessage = "Mevcut şifre boş olamaz.")]
    public string CurrentPassword { get; set; } = "";

    [Required(ErrorMessage = "Yeni şifre boş olamaz.")]
    [MinLength(8, ErrorMessage = "Şifre en az 8 karakter olmalı.")]
    public string NewPassword { get; set; } = "";

    [Compare(nameof(NewPassword), ErrorMessage = "Yeni şifreler eşleşmiyor.")]
    public string NewPasswordConfirm { get; set; } = "";
}

public sealed class AccountController(ApiClient api, ILogger<AccountController> logger) : Controller
{
    [HttpGet, AllowAnonymous]
    public async Task<IActionResult> Login(string? returnUrl, CancellationToken ct)
    {
        if (User.Identity?.IsAuthenticated == true) return RedirectToLocal(returnUrl);
        var model = new LoginViewModel { ReturnUrl = returnUrl };
        await FillCaptchaAsync(model, null, ct);
        return View(model);
    }

    [HttpPost, AllowAnonymous]
    [Microsoft.AspNetCore.RateLimiting.EnableRateLimiting(WebRateLimits.LoginPolicy)]
    public async Task<IActionResult> Login(LoginViewModel model, CancellationToken ct)
    {
        // Bot tuzağı doluysa API'ye hiç gitmeden reddet (bota sebebini söyleme)
        if (!string.IsNullOrEmpty(Request.Form["Website"].ToString()))
        {
            logger.LogWarning("Bot tuzağı tetiklendi, giriş isteği reddedildi. IP: {Ip}", HttpContext.Connection.RemoteIpAddress?.ToString());
            ModelState.AddModelError("", "Giriş yapılamadı. Lütfen tekrar deneyin.");
            model.Password = "";
            return View(model);
        }

        if (!ModelState.IsValid)
        {
            await FillCaptchaAsync(model, model.Email, ct);
            return View(model);
        }

        var captcha = Request.Form["cf-turnstile-response"].ToString();
        var result = await api.LoginAsync(new LoginRequest
        {
            Email = model.Email, Password = model.Password,
            CaptchaToken = string.IsNullOrWhiteSpace(captcha) ? null : captcha
        }, ct);
        if (!result.Success || result.Data is null)
        {
            ModelState.AddModelError("", result.Error?.Message ?? "Giriş yapılamadı.");
            model.Password = "";
            // Hatalı denemelerden sonra güvenlik doğrulaması gerekiyorsa kutuyu göster
            await FillCaptchaAsync(model, model.Email, ct);
            return View(model);
        }

        await TokenService.SignInAsync(HttpContext, result.Data, model.RememberMe);
        return result.Data.User.MustChangePassword ? RedirectToAction(nameof(ChangePassword)) : RedirectToLocal(model.ReturnUrl);
    }

    private async Task FillCaptchaAsync(LoginViewModel model, string? email, CancellationToken ct)
    {
        var r = await api.GetLoginChallengeAsync(email, ct);
        model.CaptchaSiteKey = r.Success && r.Data?.CaptchaRequired == true ? r.Data.SiteKey : null;
    }

    [HttpGet, Authorize]
    public IActionResult ChangePassword() => View(new ChangePasswordViewModel());

    /// <summary>Hem form (ilk giriş ekranı) hem AJAX (profil sayfası) ile çalışır.</summary>
    [HttpPost, Authorize]
    [Microsoft.AspNetCore.RateLimiting.EnableRateLimiting(WebRateLimits.LoginPolicy)]
    public async Task<IActionResult> ChangePassword(ChangePasswordViewModel model, [FromServices] TokenService tokens, CancellationToken ct)
    {
        var isAjax = Request.Headers.XRequestedWith == "XMLHttpRequest";
        if (!ModelState.IsValid)
        {
            var first = ModelState.Values.SelectMany(v => v.Errors).Select(e => e.ErrorMessage).FirstOrDefault() ?? "Geçersiz veri.";
            return isAjax ? Json(new { success = false, error = new { message = first } }) : View(model);
        }

        var access = await tokens.GetAccessTokenAsync(HttpContext, ct: ct);
        if (access is null) return isAjax ? Unauthorized() : RedirectToAction(nameof(Login));

        var result = await api.ChangePasswordAsync(new ChangePasswordRequest
        {
            CurrentPassword = model.CurrentPassword,
            NewPassword = model.NewPassword,
            NewPasswordConfirm = model.NewPasswordConfirm
        }, access, ct);

        if (!result.Success || result.Data is null)
        {
            var message = result.Error?.Message ?? "Şifre değiştirilemedi.";
            if (isAjax) return Json(new { success = false, error = new { message } });
            ModelState.AddModelError("", message);
            return View(new ChangePasswordViewModel());
        }

        // Diğer cihazlardaki oturumlar kapandı; bu cihaz yeni token ile devam eder
        var auth = await HttpContext.AuthenticateAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        var persistent = auth.Properties?.Items.TryGetValue("persistent", out var p) == true && p == "1";
        await TokenService.SignInAsync(HttpContext, result.Data, persistent);

        if (isAjax) return Json(new { success = true });
        TempData["Toast"] = "Şifren değiştirildi. Hoş geldin!";
        return RedirectToAction("Index", "Home");
    }

    /// <summary>
    /// Profil resmi / ad değişince çağrılır: token'ı yeniler, böylece çerezdeki kullanıcı bilgisi
    /// (üst menüdeki resim ve isim) beklemeden güncellenir.
    /// </summary>
    [HttpPost, Authorize]
    public async Task<IActionResult> RefreshProfile([FromServices] TokenService tokens, CancellationToken ct)
    {
        // Yenileme sırasında API güncel kullanıcı bilgisini döner, TokenService çerezi yeni bilgiyle yeniden yazar
        var access = await tokens.GetAccessTokenAsync(HttpContext, forceRefresh: true, ct: ct);
        return access is null ? Unauthorized() : Json(new { success = true });
    }

    [HttpPost, Authorize]
    public async Task<IActionResult> Logout(CancellationToken ct)
    {
        var (access, refresh) = await TokenService.GetStoredTokensAsync(HttpContext);
        if (access is not null && refresh is not null)
        {
            try { await api.LogoutAsync(refresh, access, ct); }
            catch (Exception ex) { logger.LogWarning(ex, "API logout çağrısı başarısız (cookie yine de silinecek)."); }
        }

        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return RedirectToAction(nameof(Login));
    }

    private IActionResult RedirectToLocal(string? returnUrl) =>
        !string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl) ? Redirect(returnUrl) : RedirectToAction("Index", "Home");
}
