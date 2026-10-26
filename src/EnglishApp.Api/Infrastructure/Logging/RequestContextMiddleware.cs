using EnglishApp.Api.Infrastructure.Security;
using Serilog.Context;

namespace EnglishApp.Api.Infrastructure.Logging;

/// <summary>
/// Correlation ID: MVC'den gelen X-Correlation-Id başlığını alır (yoksa üretir),
/// tüm loglara ve yanıta ekler. Böylece bir hatanın iki projedeki izi tek numarayla bulunur.
/// </summary>
public sealed class CorrelationIdMiddleware(RequestDelegate next)
{
    public const string HeaderName = "X-Correlation-Id";

    public async Task InvokeAsync(HttpContext context)
    {
        var incoming = context.Request.Headers[HeaderName].ToString();
        var correlationId = IsValid(incoming) ? incoming : Guid.NewGuid().ToString("N")[..16];

        context.TraceIdentifier = correlationId;
        context.Response.Headers[HeaderName] = correlationId;

        using (LogContext.PushProperty("CorrelationId", correlationId))
        using (LogContext.PushProperty("RequestPath", context.Request.Path.Value))
        using (LogContext.PushProperty("RequestMethod", context.Request.Method))
        using (LogContext.PushProperty("ClientIp", context.Connection.RemoteIpAddress?.ToString()))
        {
            await next(context);
        }
    }

    private static bool IsValid(string value) =>
        !string.IsNullOrEmpty(value) && value.Length <= 50 && value.All(c => char.IsLetterOrDigit(c) || c == '-');
}

/// <summary>Kimlik doğrulandıktan sonra kullanıcı Id'sini loglara ekler.</summary>
public sealed class UserLogContextMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var userId = context.User.FindFirst(AppClaims.UserId)?.Value;
        if (int.TryParse(userId, out var id))
        {
            using (LogContext.PushProperty("UserId", id))
            {
                await next(context);
            }
        }
        else
        {
            await next(context);
        }
    }
}
