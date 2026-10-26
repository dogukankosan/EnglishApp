using System.Threading.RateLimiting;
using EnglishApp.Shared.Common;
using EnglishApp.Web.Services;
using Microsoft.AspNetCore.RateLimiting;

namespace EnglishApp.Web.Infrastructure;

/// <summary>
/// Web tarafı istek sınırları (API'deki sınırlardan ayrı, saldırı API'ye ulaşmadan kesilsin).
/// - Giriş / şifre değiştirme formu: IP başına dakikada 10
/// - Genel: IP başına dakikada 600 (sayfa + /bff istekleri; statik dosyalar hariç)
/// </summary>
public static class WebRateLimits
{
    public const string LoginPolicy = "login";
    public const int LoginPerMinute = 10;
    public const int GlobalPerMinute = 600;

    private static readonly string[] StaticPrefixes = ["/lib", "/css", "/js", "/favicon", "/images", "/_framework", "/robots.txt"];

    public static IServiceCollection AddWebRateLimits(this IServiceCollection services) =>
        services.AddRateLimiter(o =>
        {
            o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            o.OnRejected = async (ctx, ct) =>
            {
                var http = ctx.HttpContext;
                var logger = http.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("RateLimit");
                logger.LogWarning("Web rate limit aşıldı: {Method} {Path} {Ip}", http.Request.Method, http.Request.Path, ClientIp(http));

                // AJAX / BFF: JSON; sayfa isteği: gövde boş bırakılır, /Error/429 sayfası gösterilir
                if (http.Request.Path.StartsWithSegments("/bff") || http.Request.Headers.XRequestedWith == "XMLHttpRequest")
                {
                    http.Response.ContentType = "application/json; charset=utf-8";
                    var body = ApiResponse.Fail("TOO_MANY_REQUESTS", "Çok fazla istek gönderdiniz. Biraz bekleyip tekrar deneyin.");
                    body.CorrelationId = http.TraceIdentifier;
                    await http.Response.WriteAsJsonAsync(body, ApiClient.Json, ct);
                }
            };

            o.AddPolicy(LoginPolicy, ctx => RateLimitPartition.GetFixedWindowLimiter(
                "login:" + ClientIp(ctx),
                _ => new FixedWindowRateLimiterOptions { PermitLimit = LoginPerMinute, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));

            o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx =>
                IsStatic(ctx.Request.Path)
                    ? RateLimitPartition.GetNoLimiter("static")
                    : RateLimitPartition.GetFixedWindowLimiter("all:" + ClientIp(ctx),
                        _ => new FixedWindowRateLimiterOptions { PermitLimit = GlobalPerMinute, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
        });

    public static bool IsStatic(PathString path) => StaticPrefixes.Any(p => path.StartsWithSegments(p));

    private static string ClientIp(HttpContext ctx) => ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
}
