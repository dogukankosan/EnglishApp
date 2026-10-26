using System.Text.Json;
using Dapper;
using EnglishApp.Api.Infrastructure;
using EnglishApp.Api.Infrastructure.Data;
using EnglishApp.Api.Infrastructure.Errors;
using EnglishApp.Api.Infrastructure.Llm;
using EnglishApp.Api.Infrastructure.Security;
using EnglishApp.Api.Infrastructure.Settings;
using EnglishApp.Api.Infrastructure.Time;
using EnglishApp.Shared.Dtos;
using EnglishApp.Shared.Enums;

namespace EnglishApp.Api.Services.Ai;

public sealed record LlmJsonResult<T>(T Data, int TotalTokens, LlmProvider Provider, string Model);

/// <summary>
/// Tüm yapay zeka çağrılarının tek kapısı:
/// 1) Kullanıcının günlük token limitini kontrol eder
/// 2) Sağlayıcıları sırayla dener: Gemini Flash → Gemini Lite → OpenRouter → Groq (hepsi ücretsiz katman)
/// 3) Her denemeyi ai.Usage'a, başarılı kullanımı günlük özete yazar
/// 4) Gelen JSON'u doğrular; bozuksa sıradaki sağlayıcıya geçer
/// </summary>
public interface ILlmService
{
    Task<AiQuotaDto> GetQuotaAsync(int userId, CancellationToken ct = default);
    Task EnsureQuotaAsync(int userId, CancellationToken ct = default);
    /// <summary>Ayarlı her sağlayıcıya küçük bir istek atar (admin "test et" butonu). Kullanıcı kotasına sayılmaz.</summary>
    Task<List<ProviderTestResultDto>> TestProvidersAsync(CancellationToken ct = default);
    /// <summary>LLM dışı yapay zeka çağrılarının (ör. TTS) kullanımını kaydeder ve başarılıysa günlük token sayacına ekler.</summary>
    Task RecordExternalUsageAsync(int? userId, LlmProvider provider, string model, AiFeature feature,
        int inputTokens, int outputTokens, bool success, bool isFallback, int durationMs, string? errorCode);
    /// <param name="preferFast">true: hızlı model (Gemini Lite) önce denenir ve düşünme en aza indirilir. Sohbet gibi anlık işler için.</param>
    Task<LlmJsonResult<T>> GenerateJsonAsync<T>(int? userId, AiFeature feature, IReadOnlyList<LlmMessage> messages,
        LlmJsonSchema schema, double temperature, Func<T, bool> isValid, CancellationToken ct = default, bool preferFast = false) where T : class;
}

public sealed class LlmService(
    OpenAiCompatibleClient client,
    ISettingsService settings,
    IDbConnectionFactory db,
    ICurrentUser currentUser,
    ITrClock clock,
    IConfiguration config,
    ILogger<LlmService> logger) : ILlmService
{
    private static readonly JsonSerializerOptions ParseOptions = new(JsonSerializerDefaults.Web) { AllowTrailingCommas = true };

    public async Task<AiQuotaDto> GetQuotaAsync(int userId, CancellationToken ct = default)
    {
        await using var c = await db.OpenAsync(ct);
        var row = await c.QuerySingleAsync<(int? Limit, int Used)>(new CommandDefinition(
            """
            SELECT u.DailyTokenLimit AS Limit, ISNULL(ds.AiTokens, 0) AS Used
            FROM auth.Users u
            LEFT JOIN study.DailyStats ds ON ds.UserId = u.Id AND ds.StatDate = @today
            WHERE u.Id = @userId
            """, new { userId, today = clock.Today }, cancellationToken: ct));

        return new AiQuotaDto
        {
            Limit = row.Limit,
            Used = row.Used,
            Remaining = row.Limit is null ? null : Math.Max(0, row.Limit.Value - row.Used),
            IsConfigured = (await GetProvidersAsync(ct)).Count > 0
        };
    }

    public async Task EnsureQuotaAsync(int userId, CancellationToken ct = default)
    {
        var q = await GetQuotaAsync(userId, ct);
        if (!q.IsConfigured)
            throw new AppException("AI_NOT_CONFIGURED",
                "Yapay zeka henüz ayarlanmamış. Yönetici, Admin > Ayarlar ekranından Gemini API anahtarını girmeli.", StatusCodes.Status503ServiceUnavailable);
        if (q.Remaining is 0)
            throw new AppException(ErrorCodes.DailyLimitReached,
                "Bugünkü yapay zeka hakkın doldu. Yarın tekrar kullanabilirsin; kelime ve cümle çalışmaya devam edebilirsin.", StatusCodes.Status429TooManyRequests);
    }

    public async Task<LlmJsonResult<T>> GenerateJsonAsync<T>(int? userId, AiFeature feature, IReadOnlyList<LlmMessage> messages,
        LlmJsonSchema schema, double temperature, Func<T, bool> isValid, CancellationToken ct = default, bool preferFast = false) where T : class
    {
        if (userId is not null) await EnsureQuotaAsync(userId.Value, ct);

        var providers = await GetProvidersAsync(ct);
        if (preferFast)
        {
            // Sohbette hız önemli: Lite model önce, düşünme en az (desteklemeyen model 400 verirse istemci parametresiz tekrar dener)
            var fastEffort = await settings.GetAsync("Llm.Gemini.ChatReasoningEffort", ct);
            var liteModel = await settings.GetAsync("Llm.Gemini.LiteModel", ct);
            providers = providers
                .OrderBy(p => p.Provider == LlmProvider.Gemini && p.Model == liteModel ? 0 : 1)
                .Select(p => p.Provider == LlmProvider.Gemini && !string.IsNullOrEmpty(fastEffort) ? p with { ReasoningEffort = fastEffort } : p)
                .ToList();
        }
        var attempt = 0;
        foreach (var p in providers)
        {
            var isFallback = attempt++ > 0;
            var result = await client.CompleteAsync(p, messages, temperature, schema, ct);

            T? data = null;
            string? errorCode = result.ErrorCode;
            if (result.Success)
            {
                try
                {
                    data = JsonSerializer.Deserialize<T>(OpenAiCompatibleClient.ExtractJson(result.Content!), ParseOptions);
                    if (data is null || !isValid(data)) { data = null; errorCode = "INVALID_JSON"; }
                }
                catch (JsonException)
                {
                    errorCode = "INVALID_JSON";
                }
            }

            var ok = data is not null;
            await LogUsageAsync(userId, p, feature, result, ok, isFallback, errorCode);

            if (ok)
            {
                if (userId is not null) await AddDailyTokensAsync(userId.Value, result.TotalTokens);
                if (isFallback)
                    logger.LogWarning("Yapay zeka yedek sağlayıcıyla yanıt verdi: {Provider}/{Model}", p.Provider, p.Model);
                return new LlmJsonResult<T>(data!, result.TotalTokens, p.Provider, p.Model);
            }

            logger.LogWarning("Yapay zeka çağrısı başarısız: {Provider}/{Model} {ErrorCode} ({DurationMs} ms) {ErrorDetail}",
                p.Provider, p.Model, errorCode, result.DurationMs, result.ErrorMessage);
        }

        logger.LogError("Tüm yapay zeka sağlayıcıları başarısız oldu. Özellik: {Feature}, denenen: {Count}", feature, providers.Count);
        throw new AppException("AI_UNAVAILABLE",
            "Yapay zeka şu an yanıt veremiyor (ücretsiz günlük kota dolmuş olabilir). Biraz sonra tekrar dene.",
            StatusCodes.Status503ServiceUnavailable);
    }

    private sealed class PingResult { public string Reply { get; set; } = ""; }

    private static readonly LlmJsonSchema PingSchema = new("ping", new System.Text.Json.Nodes.JsonObject
    {
        ["type"] = "object",
        ["properties"] = new System.Text.Json.Nodes.JsonObject { ["reply"] = new System.Text.Json.Nodes.JsonObject { ["type"] = "string" } },
        ["required"] = new System.Text.Json.Nodes.JsonArray("reply"),
        ["additionalProperties"] = false
    });

    public async Task<List<ProviderTestResultDto>> TestProvidersAsync(CancellationToken ct = default)
    {
        var results = new List<ProviderTestResultDto>();
        foreach (var p in await GetProvidersAsync(ct))
        {
            var r = await client.CompleteAsync(p,
                [LlmMessage.System("You are a test endpoint. Respond ONLY with JSON: {\"reply\": \"<a short friendly English greeting>\"}"),
                 LlmMessage.User("Say hello in one short sentence.")],
                0.2, PingSchema, ct);

            string? sample = null, errorCode = r.ErrorCode;
            if (r.Success)
            {
                try
                {
                    sample = JsonSerializer.Deserialize<PingResult>(OpenAiCompatibleClient.ExtractJson(r.Content!), ParseOptions)?.Reply;
                    if (string.IsNullOrWhiteSpace(sample)) errorCode = "INVALID_JSON";
                }
                catch (JsonException) { errorCode = "INVALID_JSON"; }
            }

            var ok = errorCode is null;
            await LogUsageAsync(null, p, AiFeature.Other, r, ok, false, errorCode);
            results.Add(new ProviderTestResultDto
            {
                Provider = p.Provider.ToString(),
                Model = p.Model,
                Success = ok,
                DurationMs = r.DurationMs,
                ErrorCode = errorCode,
                ErrorMessage = ok ? null : DescribeError(errorCode, r.ErrorMessage),
                Sample = sample
            });
        }
        return results;
    }

    /// <summary>Hata kodunu admin için anlaşılır Türkçe açıklamaya çevirir.</summary>
    public static string DescribeError(string? code, string? detail) => code switch
    {
        "HTTP_401" or "HTTP_403" => "API anahtarı geçersiz ya da yetkisiz. Anahtarı kontrol et.",
        "HTTP_404" => "Model bulunamadı. Model adını sağlayıcının model listesinden kontrol et.",
        "HTTP_429" => "Kota/limit doldu. Biraz sonra tekrar dene.",
        "HTTP_400" => "İstek reddedildi (model bu isteği desteklemiyor olabilir).",
        "TIMEOUT" => "Yanıt zaman aşımına uğradı.",
        "NETWORK" => "Sağlayıcıya bağlanılamadı (internet/DNS).",
        "INVALID_JSON" or "EMPTY_CONTENT" => "Yanıt geldi ama beklenen formatta değil.",
        _ when code?.StartsWith("HTTP_5") == true => "Sağlayıcı tarafında geçici sorun.",
        _ => code ?? "Bilinmeyen hata"
    } + (string.IsNullOrWhiteSpace(detail) ? "" : $" ({(detail.Length > 200 ? detail[..200] + "…" : detail)})");

    public async Task RecordExternalUsageAsync(int? userId, LlmProvider provider, string model, AiFeature feature,
        int inputTokens, int outputTokens, bool success, bool isFallback, int durationMs, string? errorCode)
    {
        var cfg = new LlmProviderConfig(provider, "", "", model, null);
        var r = new LlmCallResult(success, null, inputTokens, outputTokens, inputTokens + outputTokens, durationMs, errorCode, null, false);
        await LogUsageAsync(userId, cfg, feature, r, success, isFallback, errorCode);
        if (success && userId is not null) await AddDailyTokensAsync(userId.Value, inputTokens + outputTokens);
    }

    /// <summary>Ayarı yapılmış sağlayıcılar, deneme sırasına göre.</summary>
    private async Task<List<LlmProviderConfig>> GetProvidersAsync(CancellationToken ct)
    {
        var list = new List<LlmProviderConfig>();

        var geminiKey = await settings.GetAsync("Llm.Gemini.ApiKey", ct);
        if (!string.IsNullOrWhiteSpace(geminiKey))
        {
            var effort = await settings.GetAsync("Llm.Gemini.ReasoningEffort", ct);
            var main = await settings.GetAsync("Llm.Gemini.Model", ct);
            var lite = await settings.GetAsync("Llm.Gemini.LiteModel", ct);
            var url = OpenAiCompatibleClient.BaseUrlFor(LlmProvider.Gemini, config);
            if (!string.IsNullOrWhiteSpace(main)) list.Add(new(LlmProvider.Gemini, url, geminiKey, main, effort));
            // Ücretsiz kota model başına: ana model dolunca Lite ile devam
            if (!string.IsNullOrWhiteSpace(lite) && lite != main) list.Add(new(LlmProvider.Gemini, url, geminiKey, lite, effort));
        }

        // Groq çok hızlı ve kararlı, OpenRouter'ın bedava havuzu yavaş/yoğun olabiliyor: önce Groq, en son OpenRouter
        foreach (var (provider, prefix) in new[] { (LlmProvider.Groq, "Llm.Groq"), (LlmProvider.OpenRouter, "Llm.OpenRouter") })
        {
            var key = await settings.GetAsync(prefix + ".ApiKey", ct);
            var model = await settings.GetAsync(prefix + ".Model", ct);
            if (!string.IsNullOrWhiteSpace(key) && !string.IsNullOrWhiteSpace(model))
                list.Add(new(provider, OpenAiCompatibleClient.BaseUrlFor(provider, config), key, model, null));
        }
        return list;
    }

    private async Task LogUsageAsync(int? userId, LlmProviderConfig p, AiFeature feature, LlmCallResult r, bool ok, bool isFallback, string? errorCode)
    {
        try
        {
            await using var c = await db.OpenAsync();
            await c.ExecuteAsync(
                """
                INSERT INTO ai.Usage (UserId, Provider, Model, Feature, InputTokens, OutputTokens, IsSuccess, IsFallback, DurationMs, ErrorCode, CorrelationId, CreatedAt)
                VALUES (@userId, @provider, @model, @feature, @input, @output, @ok, @isFallback, @ms, @errorCode, @correlationId, @now)
                """,
                new
                {
                    userId, provider = (byte)p.Provider, model = p.Model, feature = (byte)feature,
                    input = r.InputTokens, output = r.OutputTokens, ok, isFallback, ms = r.DurationMs,
                    errorCode, correlationId = currentUser.CorrelationId, now = clock.Now
                });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "ai.Usage kaydı yazılamadı.");
        }
    }

    private async Task AddDailyTokensAsync(int userId, int tokens)
    {
        try
        {
            await using var c = await db.OpenAsync();
            await c.ExecuteAsync(
                """
                UPDATE study.DailyStats WITH (UPDLOCK, SERIALIZABLE)
                SET AiTokens = AiTokens + @tokens, AiMessages = AiMessages + 1
                WHERE UserId = @userId AND StatDate = @today;
                IF @@ROWCOUNT = 0
                    INSERT INTO study.DailyStats (UserId, StatDate, AiTokens, AiMessages) VALUES (@userId, @today, @tokens, 1);
                """, new { userId, tokens, today = clock.Today });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Günlük token sayacı güncellenemedi. UserId: {TargetUserId}", userId);
        }
    }
}
