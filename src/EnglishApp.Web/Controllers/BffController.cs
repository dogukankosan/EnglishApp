using System.Net;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using EnglishApp.Shared.Common;
using EnglishApp.Web.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace EnglishApp.Web.Controllers;

/// <summary>
/// BFF (Backend for Frontend) proxy: Sayfadaki AJAX istekleri buraya gelir (cookie + antiforgery ile),
/// buradan API'ye "Authorization: Bearer" ile iletilir. Tarayıcı token'ı hiç görmez, CORS gerekmez.
/// /bff/xyz → API /api/v1/xyz
/// </summary>
[Authorize]
[Route("bff")]
public sealed partial class BffController(
    IHttpClientFactory httpFactory,
    TokenService tokens,
    ILogger<BffController> logger) : Controller
{
    [GeneratedRegex(@"^[a-zA-Z0-9][a-zA-Z0-9/_\-\.]{0,200}$")]
    private static partial Regex SafePath();

    // auth/* MVC tarafında (cookie güncellemesi gerektiği için), jobs/* dış cron için: proxy'den geçmez
    private static readonly string[] BlockedPrefixes = ["auth/", "jobs/"];

    [AcceptVerbs("GET", "POST", "PUT", "DELETE")]
    [Route("{**path}")]
    [RequestSizeLimit(5 * 1024 * 1024)]
    public async Task Proxy(string path, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(path) || !SafePath().IsMatch(path) || path.Contains("..") ||
            BlockedPrefixes.Any(p => path.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
        {
            await WriteErrorAsync(HttpStatusCode.NotFound, "NOT_FOUND", "Geçersiz adres.");
            return;
        }

        var accessToken = await tokens.GetAccessTokenAsync(HttpContext, ct: ct);
        if (accessToken is null)
        {
            await SessionExpiredAsync();
            return;
        }

        // Gövdeyi bir kez oku: 401 gelirse yenilenen token ile tekrar gönderebilmek için
        byte[]? body = null;
        if (Request.ContentLength > 0 || Request.Headers.TransferEncoding.Count > 0)
        {
            // CSRF doğrulaması formu okumuş olabilir: baştan oku (Program.cs'de EnableBuffering açık)
            if (Request.Body.CanSeek) Request.Body.Position = 0;
            using var ms = new MemoryStream();
            await Request.Body.CopyToAsync(ms, ct);
            body = ms.ToArray();
        }

        var response = await SendAsync(path, body, accessToken, ct);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            response.Dispose();
            accessToken = await tokens.GetAccessTokenAsync(HttpContext, forceRefresh: true, ct: ct);
            if (accessToken is null)
            {
                await SessionExpiredAsync();
                return;
            }
            response = await SendAsync(path, body, accessToken, ct);
        }

        using (response)
        {
            Response.StatusCode = (int)response.StatusCode;
            var contentType = response.Content.Headers.ContentType?.ToString();
            if (contentType is not null) Response.ContentType = contentType;
            if (response.Headers.CacheControl is not null) Response.Headers.CacheControl = response.Headers.CacheControl.ToString();
            // Dosya indirmelerinde (Excel vb.) dosya adı tarayıcıya ulaşsın
            if (response.Content.Headers.ContentDisposition is not null)
                Response.Headers.ContentDisposition = response.Content.Headers.ContentDisposition.ToString();
            await response.Content.CopyToAsync(Response.Body, ct);
        }
    }

    private async Task<HttpResponseMessage> SendAsync(string path, byte[]? body, string accessToken, CancellationToken ct)
    {
        var http = httpFactory.CreateClient("Api");
        var request = new HttpRequestMessage(new HttpMethod(Request.Method), $"api/v1/{path}{Request.QueryString}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        ApiClient.AddForwardHeaders(request, HttpContext);

        if (body is not null)
        {
            request.Content = new ByteArrayContent(body);
            if (!string.IsNullOrEmpty(Request.ContentType))
                request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(Request.ContentType);
        }

        try
        {
            return await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            logger.LogError(ex, "API'ye ulaşılamadı: {Method} {Path}", Request.Method, path);
            var json = System.Text.Json.JsonSerializer.Serialize(
                ApiResponse.Fail("API_UNREACHABLE", "Sunucuya ulaşılamadı. Lütfen biraz sonra tekrar deneyin."), ApiClient.Json);
            return new HttpResponseMessage(HttpStatusCode.BadGateway)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
            };
        }
    }

    private async Task SessionExpiredAsync()
    {
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        await WriteErrorAsync(HttpStatusCode.Unauthorized, "SESSION_EXPIRED", "Oturumunuzun süresi doldu. Lütfen tekrar giriş yapın.");
    }

    private async Task WriteErrorAsync(HttpStatusCode status, string code, string message)
    {
        var body = ApiResponse.Fail(code, message);
        body.CorrelationId = HttpContext.TraceIdentifier;
        Response.StatusCode = (int)status;
        await Response.WriteAsJsonAsync(body, ApiClient.Json);
    }
}
