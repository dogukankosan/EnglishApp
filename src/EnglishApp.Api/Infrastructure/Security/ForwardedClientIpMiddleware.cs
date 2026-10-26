using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace EnglishApp.Api.Infrastructure.Security;

/// <summary>
/// İstekler MVC (BFF) üzerinden geldiği için API herkesi MVC sunucusunun IP'si sanar.
/// MVC, ortak gizli anahtarla (Api:BffKey) gerçek kullanıcı IP'sini gönderir; anahtar doğruysa ona güvenilir.
/// Anahtarsız gelen X-Client-Ip başlığı yok sayılır (sahte IP'ye karşı).
/// </summary>
public sealed class ForwardedClientIpMiddleware(RequestDelegate next, IConfiguration config)
{
    private readonly byte[]? _key = string.IsNullOrEmpty(config["Bff:SharedKey"]) ? null : Encoding.UTF8.GetBytes(config["Bff:SharedKey"]!);

    public Task InvokeAsync(HttpContext context)
    {
        if (_key is not null
            && context.Request.Headers.TryGetValue("X-Bff-Key", out var key)
            && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(key.ToString()), _key)
            && IPAddress.TryParse(context.Request.Headers["X-Client-Ip"].ToString(), out var ip))
        {
            context.Connection.RemoteIpAddress = ip;
        }
        return next(context);
    }
}
