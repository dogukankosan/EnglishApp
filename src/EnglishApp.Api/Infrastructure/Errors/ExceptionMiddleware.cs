using System.Text.Json;
using EnglishApp.Api.Infrastructure.Logging;
using EnglishApp.Shared.Common;
using FluentValidation;

namespace EnglishApp.Api.Infrastructure.Errors;

/// <summary>
/// Global hata yakalayıcı. Yakalanmamış her hata detaylı loglanır,
/// kullanıcıya iç detay sızdırılmadan standart yanıt döner.
/// </summary>
public sealed class ExceptionMiddleware(RequestDelegate next, ILogger<ExceptionMiddleware> logger, IHostEnvironment env)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (AppException ex)
        {
            logger.LogInformation("İş kuralı hatası {ErrorCode}: {ErrorMessage}", ex.Code, ex.Message);
            await WriteAsync(context, ex.StatusCode, ApiResponse.Fail(ex.Code, ex.Message));
        }
        catch (ValidationException ex)
        {
            var details = ex.Errors.Select(e => e.ErrorMessage).Distinct().ToList();
            logger.LogInformation("Doğrulama hatası: {@ValidationErrors}", details);
            await WriteAsync(context, StatusCodes.Status400BadRequest,
                ApiResponse.Fail(ErrorCodes.ValidationError, details.FirstOrDefault() ?? "Geçersiz veri.", details));
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // İstemci bağlantıyı kapattı, hata değil.
            logger.LogDebug("İstek istemci tarafından iptal edildi.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "Yakalanmamış hata. {RequestMethod} {RequestPath}{QueryString}",
                context.Request.Method, context.Request.Path, LogSanitizer.SafeQuery(context.Request.QueryString.Value));

            var message = env.IsDevelopment()
                ? $"Bir hata oluştu: {ex.Message}"
                : "Beklenmeyen bir hata oluştu. Lütfen daha sonra tekrar deneyin.";
            await WriteAsync(context, StatusCodes.Status500InternalServerError, ApiResponse.Fail(ErrorCodes.InternalError, message));
        }
    }

    private static async Task WriteAsync(HttpContext context, int statusCode, ApiResponse body)
    {
        if (context.Response.HasStarted) return;

        body.CorrelationId = context.TraceIdentifier;
        context.Response.Clear();
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json; charset=utf-8";
        await context.Response.WriteAsync(JsonSerializer.Serialize(body, JsonDefaults.Options));
    }
}
