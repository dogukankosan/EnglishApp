using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using EnglishApp.Shared.Enums;

namespace EnglishApp.Api.Infrastructure.Llm;

public sealed record LlmMessage(string Role, string Content)
{
    public static LlmMessage System(string c) => new("system", c);
    public static LlmMessage User(string c) => new("user", c);
    public static LlmMessage Assistant(string c) => new("assistant", c);
}

/// <summary>JSON çıktı istenirse şeması (Gemini'de birebir uygulanır, diğerlerinde "json_object").</summary>
public sealed record LlmJsonSchema(string Name, JsonObject Schema);

public sealed record LlmProviderConfig(LlmProvider Provider, string BaseUrl, string ApiKey, string Model, string? ReasoningEffort);

public sealed record LlmCallResult(
    bool Success,
    string? Content,
    int InputTokens,
    int OutputTokens,
    int TotalTokens,
    int DurationMs,
    string? ErrorCode,
    string? ErrorMessage,
    bool Retryable);

/// <summary>
/// OpenAI uyumlu /chat/completions ucuna tek istek atar.
/// Gemini, OpenRouter ve Groq aynı formatı desteklediği için tek istemci yeterli.
/// </summary>
public sealed class OpenAiCompatibleClient(IHttpClientFactory httpFactory, ILogger<OpenAiCompatibleClient> logger)
{
    public const string HttpClientName = "Llm";

    public static readonly IReadOnlyDictionary<LlmProvider, string> DefaultBaseUrls = new Dictionary<LlmProvider, string>
    {
        [LlmProvider.Gemini] = "https://generativelanguage.googleapis.com/v1beta/openai/",
        [LlmProvider.OpenRouter] = "https://openrouter.ai/api/v1/",
        [LlmProvider.Groq] = "https://api.groq.com/openai/v1/"
    };

    /// <summary>
    /// Sağlayıcı adresi. Sadece geliştirme/test için appsettings'te "Llm:BaseUrls:Gemini" ile değiştirilebilir
    /// (ör. yerel sahte sunucu). Canlıda tanımlanmaz.
    /// </summary>
    public static string BaseUrlFor(LlmProvider provider, IConfiguration config)
    {
        var overrideUrl = config[$"Llm:BaseUrls:{provider}"];
        return string.IsNullOrWhiteSpace(overrideUrl) ? DefaultBaseUrls[provider] : overrideUrl.TrimEnd('/') + "/";
    }

    public async Task<LlmCallResult> CompleteAsync(LlmProviderConfig cfg, IReadOnlyList<LlmMessage> messages,
        double temperature, LlmJsonSchema? schema, CancellationToken ct)
    {
        var first = await SendAsync(cfg, messages, temperature, schema, cfg.ReasoningEffort, ct);

        // Bazı modeller reasoning_effort parametresini tanımaz: bir kez onsuz dene
        if (!first.Success && first.ErrorCode == "HTTP_400" && !string.IsNullOrEmpty(cfg.ReasoningEffort))
        {
            logger.LogWarning("{Provider}/{Model} reasoning_effort'u kabul etmedi, parametresiz tekrar deneniyor.", cfg.Provider, cfg.Model);
            return await SendAsync(cfg, messages, temperature, schema, null, ct);
        }
        return first;
    }

    private async Task<LlmCallResult> SendAsync(LlmProviderConfig cfg, IReadOnlyList<LlmMessage> messages,
        double temperature, LlmJsonSchema? schema, string? reasoningEffort, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["model"] = cfg.Model,
            ["temperature"] = temperature,
            ["messages"] = new JsonArray(messages.Select(m => (JsonNode)new JsonObject { ["role"] = m.Role, ["content"] = m.Content }).ToArray())
        };

        if (schema is not null)
        {
            body["response_format"] = cfg.Provider == LlmProvider.Gemini
                ? new JsonObject
                {
                    ["type"] = "json_schema",
                    ["json_schema"] = new JsonObject { ["name"] = schema.Name, ["schema"] = schema.Schema.DeepClone(), ["strict"] = true }
                }
                : new JsonObject { ["type"] = "json_object" };
        }
        if (!string.IsNullOrEmpty(reasoningEffort)) body["reasoning_effort"] = reasoningEffort;

        using var request = new HttpRequestMessage(HttpMethod.Post, cfg.BaseUrl + "chat/completions")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", cfg.ApiKey);
        if (cfg.Provider == LlmProvider.OpenRouter) request.Headers.TryAddWithoutValidation("X-Title", "EnglishApp");

        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            using var response = await httpFactory.CreateClient(HttpClientName).SendAsync(request, ct);
            var text = await response.Content.ReadAsStringAsync(ct);
            sw.Stop();
            var ms = (int)sw.ElapsedMilliseconds;

            if (!response.IsSuccessStatusCode)
            {
                var status = (int)response.StatusCode;
                // 429 kota, 5xx sunucu sorunu, 401/403 hatalı anahtar: hepsinde sıradaki sağlayıcıya geçilir
                var retryable = status is 429 or 408 or >= 500 or 401 or 403 or 404;
                return new LlmCallResult(false, null, 0, 0, 0, ms, $"HTTP_{status}", Truncate(text, 500), retryable);
            }

            var json = JsonNode.Parse(text);
            var content = json?["choices"]?[0]?["message"]?["content"]?.GetValue<string>();
            var usage = json?["usage"];
            var input = usage?["prompt_tokens"]?.GetValue<int>() ?? 0;
            var output = usage?["completion_tokens"]?.GetValue<int>() ?? 0;
            var total = usage?["total_tokens"]?.GetValue<int>() ?? input + output;

            if (string.IsNullOrWhiteSpace(content))
                return new LlmCallResult(false, null, input, output, total, ms, "EMPTY_CONTENT", Truncate(text, 500), true);

            // Kullanım bilgisi gelmezse kabaca tahmin (≈4 karakter = 1 token)
            if (total == 0)
            {
                input = messages.Sum(m => m.Content.Length) / 4;
                output = content.Length / 4;
                total = input + output;
            }
            return new LlmCallResult(true, content, input, output, total, ms, null, null, false);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return new LlmCallResult(false, null, 0, 0, 0, (int)sw.ElapsedMilliseconds, "TIMEOUT", "İstek zaman aşımına uğradı.", true);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException)
        {
            return new LlmCallResult(false, null, 0, 0, 0, (int)sw.ElapsedMilliseconds,
                ex is HttpRequestException ? "NETWORK" : "BAD_RESPONSE", ex.Message, true);
        }
    }

    private static string? Truncate(string? s, int max) => s is null || s.Length <= max ? s : s[..max];

    /// <summary>Model bazen JSON'u ```json ... ``` içine koyar; ilk { ile son } arasını alır.</summary>
    public static string ExtractJson(string content)
    {
        var start = content.IndexOf('{');
        var end = content.LastIndexOf('}');
        return start >= 0 && end > start ? content[start..(end + 1)] : content;
    }
}
