using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Serilog.Context;

namespace EnglishApp.Web.Controllers;

public sealed class ErrorViewModel
{
    public int StatusCode { get; init; }
    public string Title { get; init; } = "";
    public string Message { get; init; } = "";
    public string Icon { get; init; } = "bi-exclamation-circle";
    public string? CorrelationId { get; init; }
    public string? OriginalPath { get; init; }
}

/// <summary>
/// 404 / 403 / 500 sayfaları. Hem gerçek hatalarda (yeniden çalıştırma ile) hem de doğrudan
/// /Error/404, /Error/500 adresinden açılabilir (görünümü kontrol etmek için).
/// </summary>
[AllowAnonymous]
[IgnoreAntiforgeryToken]
[ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
public sealed class ErrorController(ILogger<ErrorController> logger) : Controller
{
    [Route("Error/{code:int}")]
    public IActionResult Status(int code)
    {
        var original = HttpContext.Features.Get<IStatusCodeReExecuteFeature>();
        var exception = HttpContext.Features.Get<IExceptionHandlerPathFeature>();

        // Yakalanmamış hata: correlation ID ile detaylı logla (kullanıcıya sadece kod gösterilir)
        if (exception?.Error is { } ex)
        {
            code = 500;
            using (LogContext.PushProperty("CorrelationId", HttpContext.TraceIdentifier))
            using (LogContext.PushProperty("RequestPath", exception.Path))
            {
                logger.LogError(ex, "Web'de yakalanmamış hata: {Path}", exception.Path);
            }
        }

        var vm = Build(code, HttpContext.TraceIdentifier, original?.OriginalPath ?? exception?.Path);
        Response.StatusCode = code is >= 400 and < 600 ? code : 500;
        return View("Status", vm);
    }

    private static ErrorViewModel Build(int code, string correlationId, string? path) => code switch
    {
        404 => new()
        {
            StatusCode = 404, Icon = "bi-signpost-split", OriginalPath = path,
            Title = "Sayfa bulunamadı",
            Message = "Aradığın sayfa taşınmış, silinmiş ya da hiç var olmamış olabilir. Yolunu kaybetmiş bir turist gibi hissetme, seni geri götürelim."
        },
        403 => new()
        {
            StatusCode = 403, Icon = "bi-shield-lock", OriginalPath = path,
            Title = "Bu sayfaya erişimin yok",
            Message = "Bu sayfayı görmek için yetkin bulunmuyor. Bir hata olduğunu düşünüyorsan yöneticine haber ver."
        },
        401 => new()
        {
            StatusCode = 401, Icon = "bi-person-lock", OriginalPath = path,
            Title = "Giriş yapman gerekiyor",
            Message = "Bu sayfayı görmek için önce giriş yapmalısın."
        },
        429 => new()
        {
            StatusCode = 429, Icon = "bi-hourglass-split", OriginalPath = path,
            Title = "Biraz yavaşla",
            Message = "Kısa sürede çok fazla istek gönderdin. Bir dakika bekleyip tekrar dene."
        },
        >= 500 => new()
        {
            StatusCode = code, Icon = "bi-tools", CorrelationId = correlationId, OriginalPath = path,
            Title = "Bir şeyler ters gitti",
            Message = "Beklenmeyen bir hata oluştu. Hata kayıt altına alındı. Biraz sonra tekrar dene; sorun devam ederse aşağıdaki kodu yöneticine ilet."
        },
        _ => new()
        {
            StatusCode = code, Icon = "bi-exclamation-circle", OriginalPath = path,
            Title = "İstek tamamlanamadı",
            Message = "İsteğin işlenirken bir sorun oluştu. Lütfen tekrar dene."
        }
    };
}
