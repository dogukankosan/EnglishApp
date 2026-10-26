using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using EnglishApp.Shared.Common;
using EnglishApp.Shared.Dtos;
using EnglishApp.Web.Infrastructure;

namespace EnglishApp.Web.Services;

/// <summary>MVC sunucusunun API ile konuştuğu istemci (login, refresh, şifre değişimi).</summary>
public sealed class ApiClient(HttpClient http, IHttpContextAccessor accessor, ILogger<ApiClient> logger)
{
    public static readonly JsonSerializerOptions Json = CreateJson();

    private static JsonSerializerOptions CreateJson()
    {
        var o = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        o.Converters.Add(new JsonStringEnumConverter());
        return o;
    }

    public Task<ApiResponse<TokenResponse>> LoginAsync(LoginRequest req, CancellationToken ct) =>
        SendAsync<TokenResponse>(HttpMethod.Post, "api/v1/auth/login", req, null, ct);

    public Task<ApiResponse<LoginChallengeDto>> GetLoginChallengeAsync(string? email, CancellationToken ct) =>
        SendAsync<LoginChallengeDto>(HttpMethod.Get, "api/v1/auth/login-challenge" + (string.IsNullOrWhiteSpace(email) ? "" : "?email=" + Uri.EscapeDataString(email.Trim())), null, null, ct);

    public Task<ApiResponse<TokenResponse>> RefreshAsync(string refreshToken, CancellationToken ct) =>
        SendAsync<TokenResponse>(HttpMethod.Post, "api/v1/auth/refresh", new RefreshRequest { RefreshToken = refreshToken }, null, ct);

    public Task<ApiResponse<TokenResponse>> ChangePasswordAsync(ChangePasswordRequest req, string accessToken, CancellationToken ct) =>
        SendAsync<TokenResponse>(HttpMethod.Post, "api/v1/auth/change-password", req, accessToken, ct);

    public Task<ApiResponse<object>> LogoutAsync(string refreshToken, string accessToken, CancellationToken ct) =>
        SendAsync<object>(HttpMethod.Post, "api/v1/auth/logout", new LogoutRequest { RefreshToken = refreshToken }, accessToken, ct);

    private async Task<ApiResponse<T>> SendAsync<T>(HttpMethod method, string url, object? body, string? accessToken, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, url);
        if (body is not null) request.Content = JsonContent.Create(body, options: Json);
        if (accessToken is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        AddForwardHeaders(request, accessor.HttpContext);

        try
        {
            using var response = await http.SendAsync(request, ct);
            var result = await response.Content.ReadFromJsonAsync<ApiResponse<T>>(Json, ct);
            return result ?? Fail<T>("API_EMPTY_RESPONSE", "API'den boş yanıt geldi.");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            logger.LogError(ex, "API çağrısı başarısız: {Method} {Url}", method, url);
            return Fail<T>("API_UNREACHABLE", "Sunucuya ulaşılamadı. Lütfen biraz sonra tekrar deneyin.");
        }
    }

    /// <summary>Correlation ID, gerçek istemci IP'si ve tarayıcı bilgisini API'ye taşır.</summary>
    public static void AddForwardHeaders(HttpRequestMessage request, HttpContext? context)
    {
        if (context is null) return;
        request.Headers.TryAddWithoutValidation(CorrelationIdMiddleware.HeaderName, context.TraceIdentifier);
        var ua = context.Request.Headers.UserAgent.ToString();
        if (!string.IsNullOrEmpty(ua)) request.Headers.TryAddWithoutValidation("User-Agent", ua);

        // API, gerçek kullanıcı IP'sine göre rate limit ve log tutsun diye (sadece ortak anahtarla güvenilir)
        var bffKey = context.RequestServices.GetRequiredService<IConfiguration>()["Api:BffKey"];
        var ip = context.Connection.RemoteIpAddress?.ToString();
        if (!string.IsNullOrEmpty(bffKey) && !string.IsNullOrEmpty(ip))
        {
            request.Headers.TryAddWithoutValidation("X-Bff-Key", bffKey);
            request.Headers.TryAddWithoutValidation("X-Client-Ip", ip);
        }
    }

    private static ApiResponse<T> Fail<T>(string code, string message) =>
        new() { Success = false, Error = new ApiError(code, message) };
}
