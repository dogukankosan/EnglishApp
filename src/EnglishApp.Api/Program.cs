using Microsoft.AspNetCore.DataProtection;
using System.Globalization;
using System.Threading.RateLimiting;
using EnglishApp.Api.Data;
using EnglishApp.Api.Infrastructure;
using EnglishApp.Api.Infrastructure.Data;
using EnglishApp.Api.Infrastructure.Errors;
using EnglishApp.Api.Infrastructure.Logging;
using EnglishApp.Api.Infrastructure.Mail;
using EnglishApp.Api.Infrastructure.Security;
using EnglishApp.Api.Infrastructure.Settings;
using EnglishApp.Api.Infrastructure.Time;
using EnglishApp.Api.Infrastructure.Validation;
using EnglishApp.Api.Services;
using EnglishApp.Shared.Common;
using FluentValidation;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Mvc;
using Microsoft.IdentityModel.Tokens;
using Serilog;

// Sunucunun kültürüne güvenme: API'de sayı/tarih her zaman invariant
CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.InvariantCulture;

Log.Logger = new LoggerConfiguration()
    .Enrich.With(new TrTimeEnricher())
    .WriteTo.Console()
    .CreateBootstrapLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);

    builder.Host.UseSerilog((ctx, services, cfg) =>
        LoggingSetup.Configure(cfg.ReadFrom.Services(services), ctx.Configuration, ctx.HostingEnvironment));

    // ---------- Altyapı ----------
    builder.Services.AddHttpContextAccessor();
    builder.Services.AddMemoryCache();
    // ASP.NET'in iç veri koruma anahtarları kalıcı klasörde dursun (her açılışta yeniden üretilip log kirletmesin)
    builder.Services.AddDataProtection()
        .SetApplicationName("EnglishApp.Api")
        .PersistKeysToFileSystem(new DirectoryInfo(
            builder.Configuration["DataProtection:KeysPath"] ?? Path.Combine(builder.Environment.ContentRootPath, "App_Data", "keys")));
    builder.Services.AddSingleton<ITrClock, TrClock>();
    builder.Services.AddSingleton<IDbConnectionFactory, DbConnectionFactory>();
    builder.Services.AddSingleton<IEncryptionService, EncryptionService>();
    builder.Services.AddSingleton<IPasswordService, PasswordService>();
    builder.Services.AddSingleton<IFileStorage, FileStorage>();
    builder.Services.AddSingleton<ISettingsService, SettingsService>();
builder.Services.AddSingleton<ILoginAttemptTracker, LoginAttemptTracker>();
builder.Services.AddScoped<ITurnstileService, TurnstileService>();
builder.Services.AddHttpClient(TurnstileService.HttpClientName, c => c.Timeout = TimeSpan.FromSeconds(10));
    builder.Services.AddScoped<ICurrentUser, CurrentUser>();
    builder.Services.AddScoped<IAuditService, AuditService>();
    builder.Services.AddScoped<IMailService, MailService>();

    // ---------- Repository'ler ----------
    builder.Services.AddScoped<IUserRepository, UserRepository>();
    builder.Services.AddScoped<IRefreshTokenRepository, RefreshTokenRepository>();
    builder.Services.AddScoped<IUserSettingsRepository, UserSettingsRepository>();
    builder.Services.AddScoped<IContentRepository, ContentRepository>();
    builder.Services.AddScoped<IStudyRepository, StudyRepository>();
    builder.Services.AddScoped<IStatsRepository, StatsRepository>();

    // ---------- Servisler ----------
    builder.Services.AddScoped<IAuthStateCache, AuthStateCache>();
    builder.Services.AddScoped<IAuthService, AuthService>();
    builder.Services.AddScoped<IProfileService, ProfileService>();
    builder.Services.AddScoped<IAdminUserService, AdminUserService>();
    builder.Services.AddScoped<IAdminSettingsService, AdminSettingsService>();
    builder.Services.AddScoped<ILogQueryService, LogQueryService>();
    builder.Services.AddScoped<IScenarioService, ScenarioService>();
    builder.Services.AddScoped<IStudyService, StudyService>();
    builder.Services.AddScoped<IStatsService, StatsService>();
builder.Services.AddScoped<ILeaderboardService, LeaderboardService>();
    builder.Services.AddScoped<IPlacementService, PlacementService>();
    builder.Services.AddScoped<IJobService, JobService>();
    builder.Services.AddScoped<IContentSeeder, ContentSeeder>();
    builder.Services.AddSingleton<StartupSeeder>();

    // ---------- Yapay zeka ----------
    builder.Services.AddHttpClient(EnglishApp.Api.Infrastructure.Llm.OpenAiCompatibleClient.HttpClientName,
        // Üst sınır. Asıl süre istek başına verilir (sohbet 25 sn, içerik üretimi daha uzun), sonra sıradaki modele geçilir.
        c => c.Timeout = TimeSpan.FromMinutes(2));
    builder.Services.AddSingleton<EnglishApp.Api.Infrastructure.Llm.OpenAiCompatibleClient>();
    builder.Services.AddScoped<EnglishApp.Api.Services.Ai.ILlmService, EnglishApp.Api.Services.Ai.LlmService>();
    builder.Services.AddScoped<EnglishApp.Api.Services.Ai.IConversationService, EnglishApp.Api.Services.Ai.ConversationService>();
    builder.Services.AddScoped<EnglishApp.Api.Services.Ai.IWritingService, EnglishApp.Api.Services.Ai.WritingService>();
    builder.Services.AddScoped<EnglishApp.Api.Services.Ai.ITtsService, EnglishApp.Api.Services.Ai.GeminiTtsService>();
    builder.Services.AddScoped<EnglishApp.Api.Services.Ai.ISttService, EnglishApp.Api.Services.Ai.GroqWhisperSttService>();
    builder.Services.AddScoped<EnglishApp.Api.Services.Ai.PersonalContentWriter>();
    builder.Services.AddScoped<EnglishApp.Api.Services.Ai.IListeningQuizService, EnglishApp.Api.Services.Ai.ListeningQuizService>();
    builder.Services.AddScoped<EnglishApp.Api.Services.Ai.ICaptureService, EnglishApp.Api.Services.Ai.CaptureService>();
    builder.Services.AddScoped<EnglishApp.Api.Services.Ai.ITravelKitService, EnglishApp.Api.Services.Ai.TravelKitService>();
    builder.Services.AddScoped<EnglishApp.Api.Services.Ai.IAdminContentService, EnglishApp.Api.Services.Ai.AdminContentService>();
builder.Services.AddScoped<IDictationService, DictationService>();
builder.Services.AddScoped<IMediaService, MediaService>();
builder.Services.AddScoped<IVideoService, VideoService>();
builder.Services.AddScoped<EnglishApp.Api.Services.Ai.ICoachService, EnglishApp.Api.Services.Ai.CoachService>();
builder.Services.AddScoped<EnglishApp.Api.Services.Ai.IReadingService, EnglishApp.Api.Services.Ai.ReadingService>();
    builder.Services.AddSingleton<EnglishApp.Api.Infrastructure.Files.IAudioStore, EnglishApp.Api.Infrastructure.Files.AudioStore>();

    builder.Services.AddValidatorsFromAssemblyContaining<Program>();
    ValidatorOptions.Global.LanguageManager.Culture = new CultureInfo("tr");

    // ---------- JWT ----------
    var jwtOptions = builder.Configuration.GetSection("Jwt").Get<JwtOptions>() ?? new JwtOptions();
    if (jwtOptions.Key.Length < 32)
        throw new InvalidOperationException("appsettings.json içinde Jwt:Key en az 32 karakter olmalı.");
    builder.Services.AddSingleton(jwtOptions);
    builder.Services.AddSingleton<IJwtTokenService, JwtTokenService>();

    builder.Services
        .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
        .AddJwtBearer(o =>
        {
            o.MapInboundClaims = false;
            o.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidIssuer = jwtOptions.Issuer,
                ValidateAudience = true,
                ValidAudience = jwtOptions.Audience,
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = jwtOptions.SigningKey,
                ValidateLifetime = true,
                ClockSkew = TimeSpan.FromSeconds(30),
                NameClaimType = AppClaims.Name,
                RoleClaimType = AppClaims.Role
            };
            o.Events = new JwtBearerEvents
            {
                // Pasife alınan / silinen / şifresi değişen kullanıcının eski token'ı reddedilir
                OnTokenValidated = async ctx =>
                {
                    var userIdValue = ctx.Principal?.FindFirst(AppClaims.UserId)?.Value;
                    var stamp = ctx.Principal?.FindFirst(AppClaims.SecurityStamp)?.Value;
                    if (!int.TryParse(userIdValue, out var userId) || stamp is null)
                    {
                        ctx.Fail("Geçersiz token.");
                        return;
                    }

                    var cache = ctx.HttpContext.RequestServices.GetRequiredService<IAuthStateCache>();
                    var state = await cache.GetAsync(userId, ctx.HttpContext.RequestAborted);
                    if (state is null || !state.IsActive || state.SecurityStamp.ToString("N") != stamp)
                        ctx.Fail("Oturum geçersiz.");
                },
                OnChallenge = async ctx =>
                {
                    ctx.HandleResponse();
                    ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    ctx.Response.ContentType = "application/json; charset=utf-8";
                    var body = ApiResponse.Fail(ErrorCodes.Unauthorized, "Oturum açmanız gerekiyor.");
                    body.CorrelationId = ctx.HttpContext.TraceIdentifier;
                    await ctx.Response.WriteAsJsonAsync(body, JsonDefaults.Options);
                },
                OnForbidden = async ctx =>
                {
                    ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                    ctx.Response.ContentType = "application/json; charset=utf-8";
                    var body = ApiResponse.Fail(ErrorCodes.Forbidden, "Bu işlem için yetkiniz yok.");
                    body.CorrelationId = ctx.HttpContext.TraceIdentifier;
                    await ctx.Response.WriteAsJsonAsync(body, JsonDefaults.Options);
                }
            };
        });
    builder.Services.AddAuthorization();

    // ---------- Rate limiting ----------
    builder.Services.AddRateLimiter(o =>
    {
        o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
        o.OnRejected = async (ctx, ct) =>
        {
            ctx.HttpContext.Response.ContentType = "application/json; charset=utf-8";
            Log.Warning("Rate limit aşıldı: {Path} {Ip}", ctx.HttpContext.Request.Path, ctx.HttpContext.Connection.RemoteIpAddress?.ToString());
            var body = ApiResponse.Fail(ErrorCodes.TooManyRequests, "Çok fazla istek gönderdiniz. Biraz bekleyip tekrar deneyin.");
            body.CorrelationId = ctx.HttpContext.TraceIdentifier;
            await ctx.HttpContext.Response.WriteAsJsonAsync(body, JsonDefaults.Options, ct);
        };
        // Login/refresh: IP başına dakikada 10
        o.AddPolicy("auth", ctx => RateLimitPartition.GetFixedWindowLimiter(
            ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
        // Yapay zeka: kullanıcı başına dakikada 20 istek (ücretsiz kotayı tek kişi bitirmesin)
        o.AddPolicy("ai", ctx => RateLimitPartition.GetFixedWindowLimiter(
            ctx.User.FindFirst(AppClaims.UserId)?.Value ?? ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions { PermitLimit = 20, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
        // Genel: kullanıcı (yoksa IP) başına dakikada 300
        o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx =>
            RateLimitPartition.GetFixedWindowLimiter(
                ctx.User.FindFirst(AppClaims.UserId)?.Value ?? ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = 300, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    });

    // ---------- MVC ----------
    builder.Services
        .AddControllers(o =>
        {
            o.Filters.Add<ValidationFilter>();
            o.Filters.Add<PasswordChangeRequiredFilter>();
        })
        .AddJsonOptions(o => JsonDefaults.Configure(o.JsonSerializerOptions))
        .ConfigureApiBehaviorOptions(o =>
        {
            // Model binding hatalarını (bozuk JSON vb.) standart formata çevir
            o.InvalidModelStateResponseFactory = ctx =>
            {
                var details = ctx.ModelState.Values.SelectMany(v => v.Errors)
                    .Select(e => string.IsNullOrEmpty(e.ErrorMessage) ? "Geçersiz veri." : e.ErrorMessage).Distinct().ToList();
                var body = ApiResponse.Fail(ErrorCodes.ValidationError, "Gönderilen veri okunamadı.", details);
                body.CorrelationId = ctx.HttpContext.TraceIdentifier;
                return new BadRequestObjectResult(body);
            };
        });

    var app = builder.Build();

    // ---------- Hosting (HTTPS / proxy) ----------
    // Hosting HTTPS'i önündeki bir sunucuda (proxy, Cloudflare) çözüp uygulamaya HTTP iletiyorsa
    // Hosting:ForwardedHeaders=true yapılır: gerçek protokol (https) ve kullanıcı IP'si başlıklardan okunur.
    // Sadece uygulamaya proxy dışından doğrudan erişilemiyorsa açılmalı (başlık sahtelenemesin).
    if (app.Configuration.GetValue("Hosting:ForwardedHeaders", false))
    {
        var fh = new Microsoft.AspNetCore.Builder.ForwardedHeadersOptions
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

    // ---------- Pipeline ----------
    app.Use((ctx, next) =>
{
    // API adresi arama motorlarında listelenmesin
    ctx.Response.Headers["X-Robots-Tag"] = "noindex, nofollow";
    // Temel güvenlik başlıkları (mobil uygulama API'ye doğrudan bağlanacak)
    ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
    ctx.Response.Headers["X-Frame-Options"] = "DENY";
    ctx.Response.Headers["Referrer-Policy"] = "no-referrer";
    return next();
});
app.UseMiddleware<ForwardedClientIpMiddleware>();
    app.UseMiddleware<CorrelationIdMiddleware>();
    app.UseSerilogRequestLogging(o =>
    {
        o.MessageTemplate = "HTTP {RequestMethod} {RequestPath} → {StatusCode} ({Elapsed:0} ms)";
        // 1 saniyeden yavaş istekler Warning, 5xx Error
        // Normal istekler 1 sn'den, yapay zeka istekleri 10 sn'den yavaşsa uyarı; yapay zeka kotası dolunca (503) uyarı
        o.GetLevel = (ctx, elapsed, ex) => RequestLogLevels.Get(ctx.Request.Path.Value, ctx.Response.StatusCode, elapsed, ex is not null, 1000) switch
        {
            RequestLogLevel.Error => Serilog.Events.LogEventLevel.Error,
            RequestLogLevel.Warning => Serilog.Events.LogEventLevel.Warning,
            _ => Serilog.Events.LogEventLevel.Information
        };
    });
    app.UseMiddleware<ExceptionMiddleware>();

    // Bilinmeyen adres (404), yanlış metot (405) gibi gövdesi boş yanıtlar da standart JSON formatında dönsün
    app.UseStatusCodePages(async ctx =>
    {
        var res = ctx.HttpContext.Response;
        var (code, message) = res.StatusCode switch
        {
            404 => (ErrorCodes.NotFound, "İstenen adres bulunamadı."),
            405 => ("METHOD_NOT_ALLOWED", "Bu adres bu istek türünü desteklemiyor."),
            415 => ("UNSUPPORTED_MEDIA_TYPE", "Gönderilen veri türü desteklenmiyor."),
            _ => ("HTTP_" + res.StatusCode, "İstek tamamlanamadı.")
        };
        var body = ApiResponse.Fail(code, message);
        body.CorrelationId = ctx.HttpContext.TraceIdentifier;
        await res.WriteAsJsonAsync(body, JsonDefaults.Options);
    });

    if (!app.Environment.IsDevelopment())
    {
        // HSTS başlığı yalnızca HTTPS isteklerinde gönderilir (döngü yapmaz)
        app.UseHsts();
        // HTTP→HTTPS yönlendirmesini hosting yapıyorsa kapalı kalır (çift yönlendirme / döngü olmasın)
        if (app.Configuration.GetValue("Hosting:HttpsRedirection", false)) app.UseHttpsRedirection();
    }

    app.UseAuthentication();
    app.UseMiddleware<UserLogContextMiddleware>();
    app.UseRateLimiter();
    app.UseAuthorization();
    app.MapControllers();
    app.MapGet("/health", () => Results.Ok(new { status = "ok" })).AllowAnonymous();

    await app.Services.GetRequiredService<StartupSeeder>().RunAsync();

    Log.Information("EnglishApp.Api başlatıldı. Ortam: {Environment}", app.Environment.EnvironmentName);
    await app.RunAsync();
}
catch (Exception ex) when (ex is not HostAbortedException)
{
    Log.Fatal(ex, "EnglishApp.Api başlatılamadı.");
    throw;
}
finally
{
    await Log.CloseAndFlushAsync();
}

public partial class Program;
