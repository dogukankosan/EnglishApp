using System.Globalization;
using EnglishApp.Web.Infrastructure;
using EnglishApp.Web.Services;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Localization;
using Microsoft.AspNetCore.Mvc;
using Serilog;

Log.Logger = new LoggerConfiguration().Enrich.With(new TrTimeEnricher()).WriteTo.Console().CreateBootstrapLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);

    builder.Host.UseSerilog((ctx, services, cfg) =>
        WebLogging.Configure(cfg.ReadFrom.Services(services), ctx.Configuration, ctx.HostingEnvironment));

    var apiBaseUrl = builder.Configuration["Api:BaseUrl"];
    if (string.IsNullOrWhiteSpace(apiBaseUrl))
        throw new InvalidOperationException("appsettings.json içinde Api:BaseUrl tanımlı değil.");

    builder.Services.AddHttpContextAccessor();
    builder.Services.AddMemoryCache();
    // Razor Türkçe karakterleri &#x131; gibi kodlamasın (güvenlik kodlaması < > & " için devam eder)
    builder.Services.Configure<Microsoft.Extensions.WebEncoders.WebEncoderOptions>(o =>
        o.TextEncoderSettings = new System.Text.Encodings.Web.TextEncoderSettings(System.Text.Unicode.UnicodeRanges.All));
    builder.Services.AddHttpClient("Api", c =>
    {
        c.BaseAddress = new Uri(apiBaseUrl.TrimEnd('/') + "/");
        c.Timeout = TimeSpan.FromSeconds(60);
    });
    builder.Services.AddHttpClient<ApiClient>(c =>
    {
        c.BaseAddress = new Uri(apiBaseUrl.TrimEnd('/') + "/");
        c.Timeout = TimeSpan.FromSeconds(30);
    });
    builder.Services.AddScoped<TokenService>();
    builder.Services.AddWebRateLimits();

    // Cookie şifreleme anahtarları diske yazılır: uygulama yeniden başlayınca oturumlar düşmesin
    builder.Services.AddDataProtection()
        .SetApplicationName("EnglishApp.Web")
        .PersistKeysToFileSystem(new DirectoryInfo(
            builder.Configuration["DataProtection:KeysPath"] ?? Path.Combine(builder.Environment.ContentRootPath, "App_Data", "keys")));

    builder.Services
        .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
        .AddCookie(o =>
        {
            o.Cookie.Name = "EnglishApp.Auth";
            o.Cookie.HttpOnly = true;
            o.Cookie.SameSite = SameSiteMode.Strict;
            o.Cookie.SecurePolicy = builder.Environment.IsDevelopment() ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
            o.LoginPath = "/Account/Login";
            o.LogoutPath = "/Account/Logout";
            o.AccessDeniedPath = "/Error/403";
            o.SlidingExpiration = false;
            o.Events = new CookieAuthenticationEvents
            {
                // AJAX isteklerinde login sayfasına yönlendirmek yerine 401/403 JSON dön
                OnRedirectToLogin = ctx => RedirectOrStatus(ctx, StatusCodes.Status401Unauthorized),
                OnRedirectToAccessDenied = ctx => RedirectOrStatus(ctx, StatusCodes.Status403Forbidden)
            };
        });
    builder.Services.AddAuthorization();

    builder.Services.AddAntiforgery(o =>
    {
        o.HeaderName = "RequestVerificationToken";
        o.Cookie.Name = "EnglishApp.Csrf";
        o.Cookie.SameSite = SameSiteMode.Strict;
        o.Cookie.HttpOnly = true;
    });

    builder.Services.AddControllersWithViews(o =>
    {
        o.Filters.Add(new AutoValidateAntiforgeryTokenAttribute());
        o.Filters.Add<PasswordChangeRedirectFilter>();
    });

    var app = builder.Build();

    // Hosting HTTPS'i önündeki bir sunucuda (proxy, Cloudflare) çözüp uygulamaya HTTP iletiyorsa
    // Hosting:ForwardedHeaders=true: gerçek protokol (https) ve kullanıcı IP'si (rate limit, log) başlıklardan okunur.
    // Sadece uygulamaya proxy dışından doğrudan erişilemiyorsa açılmalı (başlık sahtelenemesin).
    if (app.Configuration.GetValue("Hosting:ForwardedHeaders", false))
    {
        var fh = new ForwardedHeadersOptions
        {
            ForwardedHeaders = Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedFor
                             | Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedProto,
            // Cloudflare için: "CF-Connecting-IP"
            ForwardedForHeaderName = app.Configuration["Hosting:ForwardedForHeader"] ?? "X-Forwarded-For"
        };
        fh.KnownIPNetworks.Clear();
        fh.KnownProxies.Clear();
        app.UseForwardedHeaders(fh);
    }

    // Arayüz her zaman Türkçe (sunucunun dili ne olursa olsun)
    var tr = new CultureInfo("tr-TR");
    app.UseRequestLocalization(new RequestLocalizationOptions
    {
        DefaultRequestCulture = new RequestCulture(tr),
        SupportedCultures = [tr],
        SupportedUICultures = [tr],
        RequestCultureProviders = []
    });

    // 500: geliştirmede detaylı hata ekranı, canlıda şık hata sayfası (/Error/500 adresinden her zaman önizlenebilir)
    if (app.Environment.IsDevelopment())
    {
        app.UseDeveloperExceptionPage();
    }
    else
    {
        app.UseExceptionHandler("/Error/500");
        // HSTS başlığı yalnızca HTTPS isteklerinde gönderilir (döngü yapmaz)
        app.UseHsts();
        // HTTP→HTTPS yönlendirmesini hosting yapıyorsa kapalı kalır (çift yönlendirme / döngü olmasın)
        if (app.Configuration.GetValue("Hosting:HttpsRedirection", false)) app.UseHttpsRedirection();
    }

    // 404, 403 vb.: gövdesi boş hata yanıtları için hata sayfası (BFF JSON yanıtlarına dokunmaz)
    app.UseStatusCodePagesWithReExecute("/Error/{0}");

    app.Use(async (ctx, next) =>
    {
        // Temel güvenlik başlıkları
        ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
        ctx.Response.Headers["X-Frame-Options"] = "DENY";
        ctx.Response.Headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
        // Kişisel uygulama: arama motorları hiçbir sayfayı/dosyayı listelemesin
        ctx.Response.Headers["X-Robots-Tag"] = "noindex, nofollow";
        await next();
    });

    // BFF isteklerinde gövde tekrar okunabilsin: CSRF kontrolü dosyalı (multipart) formu okuyup
    // akışı tüketiyor, proxy de aynı gövdeyi API'ye iletmek zorunda
    app.Use((ctx, next) =>
    {
        if (ctx.Request.Path.StartsWithSegments("/bff")) ctx.Request.EnableBuffering();
        return next();
    });

    app.UseRouting();
    // İstek sınırı (giriş formu + genel). Statik dosyalar hariç.
    app.UseRateLimiter();
    app.UseAuthentication();
    app.UseMiddleware<CorrelationIdMiddleware>();
    app.UseSerilogRequestLogging(o =>
    {
        o.MessageTemplate = "HTTP {RequestMethod} {RequestPath} → {StatusCode} ({Elapsed:0} ms)";
        // Normal istekler 2 sn'den, yapay zeka istekleri 10 sn'den yavaşsa uyarı; yapay zeka kotası dolunca (503) uyarı
        o.GetLevel = (ctx, elapsed, ex) => EnglishApp.Shared.Common.RequestLogLevels.Get(ctx.Request.Path.Value, ctx.Response.StatusCode, elapsed, ex is not null, 2000) switch
        {
            EnglishApp.Shared.Common.RequestLogLevel.Error => Serilog.Events.LogEventLevel.Error,
            EnglishApp.Shared.Common.RequestLogLevel.Warning => Serilog.Events.LogEventLevel.Warning,
            _ => Serilog.Events.LogEventLevel.Information
        };
    });
    app.UseAuthorization();

    app.MapStaticAssets();
    app.MapControllerRoute("areas", "{area:exists}/{controller=Home}/{action=Index}/{id?}").WithStaticAssets();
    app.MapControllerRoute("default", "{controller=Home}/{action=Index}/{id?}").WithStaticAssets();

    Log.Information("EnglishApp.Web başlatıldı. API: {ApiBaseUrl}", apiBaseUrl);
    await app.RunAsync();
}
catch (Exception ex) when (ex is not HostAbortedException)
{
    Log.Fatal(ex, "EnglishApp.Web başlatılamadı.");
    throw;
}
finally
{
    await Log.CloseAndFlushAsync();
}

static Task RedirectOrStatus(Microsoft.AspNetCore.Authentication.RedirectContext<CookieAuthenticationOptions> ctx, int status)
{
    var isAjax = ctx.Request.Path.StartsWithSegments("/bff") ||
                 ctx.Request.Headers.XRequestedWith == "XMLHttpRequest";
    if (isAjax)
    {
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/json; charset=utf-8";
        var body = status == 401
            ? EnglishApp.Shared.Common.ApiResponse.Fail("SESSION_EXPIRED", "Oturumunuzun süresi doldu. Lütfen tekrar giriş yapın.")
            : EnglishApp.Shared.Common.ApiResponse.Fail("FORBIDDEN", "Bu işlem için yetkiniz yok.");
        return ctx.Response.WriteAsJsonAsync(body, ApiClient.Json);
    }
    ctx.Response.Redirect(ctx.RedirectUri);
    return Task.CompletedTask;
}
