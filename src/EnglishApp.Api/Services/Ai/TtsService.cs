using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using EnglishApp.Api.Infrastructure.Errors;
using EnglishApp.Api.Infrastructure.Security;
using EnglishApp.Api.Infrastructure.Settings;
using EnglishApp.Shared.Enums;
using FluentValidation;

namespace EnglishApp.Api.Services.Ai;

public sealed class TtsRequest
{
    public string Text { get; set; } = "";
    public string? Voice { get; set; }
}

/// <summary>
/// Gemini TTS ile doğal ses (WAV) üretir.
/// - Aynı metin + ses daha önce üretildiyse diskten döner (kota harcanmaz).
/// - Modeller sırayla denenir; kotası dolan model 10 dk dinlendirilir.
/// - Hepsi başarısızsa TTS_UNAVAILABLE döner, tarayıcı kendi sesine geçer.
/// </summary>
public interface ITtsService
{
    Task<byte[]> SynthesizeAsync(TtsRequest req, CancellationToken ct);
    Task<string> VoiceForRoleAsync(string? role, CancellationToken ct);
    /// <summary>Her TTS modelini kısa bir metinle dener (admin test butonu). Önbellek ve kota kullanılmaz.</summary>
    Task<List<EnglishApp.Shared.Dtos.ProviderTestResultDto>> TestAsync(CancellationToken ct);
}

public sealed class GeminiTtsService(
    IHttpClientFactory httpFactory,
    ISettingsService settings,
    ILlmService llm,
    ICurrentUser currentUser,
    IWebHostEnvironment env,
    IConfiguration config,
    ILogger<GeminiTtsService> logger) : ITtsService
{
    private const string DefaultBaseUrl = "https://generativelanguage.googleapis.com/v1beta/";
    private const int SampleRate = 24000;
    private static readonly ConcurrentDictionary<string, DateTime> ModelCooldownUtc = new();

    private string CacheDir => Path.Combine(config["Storage:RootPath"] ?? Path.Combine(env.ContentRootPath, "App_Data"), "tts-cache");

    public async Task<string> VoiceForRoleAsync(string? role, CancellationToken ct)
    {
        var voices = await GetVoicesAsync(ct);
        if (string.IsNullOrEmpty(role)) return voices[0];
        // Aynı rol hep aynı sesle konuşsun
        var hash = BitConverter.ToUInt32(SHA256.HashData(Encoding.UTF8.GetBytes(role.ToLowerInvariant())), 0);
        return voices[(int)(hash % (uint)voices.Count)];
    }

    public async Task<byte[]> SynthesizeAsync(TtsRequest req, CancellationToken ct)
    {
        var text = req.Text.Trim();
        var voices = await GetVoicesAsync(ct);
        var voice = voices.FirstOrDefault(v => v.Equals(req.Voice, StringComparison.OrdinalIgnoreCase)) ?? voices[0];

        if (!await settings.GetBoolAsync("Llm.Gemini.TtsEnabled", true, ct)) throw Unavailable("Doğal ses kapalı.");
        var apiKey = await settings.GetAsync("Llm.Gemini.ApiKey", ct);
        if (string.IsNullOrWhiteSpace(apiKey)) throw Unavailable("Gemini API anahtarı yok.");

        // 1) Önbellek
        var cachePath = Path.Combine(CacheDir, CacheKey(voice, text) + ".wav");
        if (File.Exists(cachePath)) return await File.ReadAllBytesAsync(cachePath, ct);

        // 2) Kota
        var userId = currentUser.Id;
        await llm.EnsureQuotaAsync(userId, ct);

        // 3) Modelleri sırayla dene
        var models = ((await settings.GetAsync("Llm.Gemini.TtsModels", ct)) ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var attempt = 0;
        foreach (var model in models)
        {
            if (ModelCooldownUtc.TryGetValue(model, out var until) && until > DateTime.UtcNow) continue;

            var sw = Stopwatch.StartNew();
            var (audio, tokens, error) = await CallInteractionsAsync(apiKey, model, voice, text, ct);
            if (audio is null && error is "HTTP_400" or "HTTP_404" or "NO_AUDIO")
                (audio, tokens, error) = await CallGenerateContentAsync(apiKey, model, voice, text, ct);
            sw.Stop();

            var ok = audio is not null;
            // Kullanım bilgisi gelmezse kaba tahmin: metin ≈ karakter/4, ses ≈ saniyede 25 token
            var input = tokens > 0 ? tokens : text.Length / 4 + 1;
            var output = ok && tokens == 0 ? (int)(audio!.Length / (SampleRate * 2.0) * 25) : 0;
            await llm.RecordExternalUsageAsync(userId, LlmProvider.Gemini, model, AiFeature.Tts, input, output, ok, attempt++ > 0, (int)sw.ElapsedMilliseconds, error);

            if (ok)
            {
                var wav = EnsureWav(audio!);
                try
                {
                    Directory.CreateDirectory(CacheDir);
                    await File.WriteAllBytesAsync(cachePath, wav, ct);
                }
                catch (Exception ex) { logger.LogWarning(ex, "TTS önbelleğe yazılamadı."); }
                return wav;
            }

            if (error is "HTTP_429") ModelCooldownUtc[model] = DateTime.UtcNow.AddMinutes(10);
            logger.LogWarning("Gemini TTS başarısız: {Model} {ErrorCode} ({DurationMs} ms)", model, error, sw.ElapsedMilliseconds);
        }

        throw Unavailable("Doğal ses şu an üretilemiyor (kota dolmuş olabilir).");
    }

    public async Task<List<EnglishApp.Shared.Dtos.ProviderTestResultDto>> TestAsync(CancellationToken ct)
    {
        var results = new List<EnglishApp.Shared.Dtos.ProviderTestResultDto>();
        var apiKey = await settings.GetAsync("Llm.Gemini.ApiKey", ct);
        if (string.IsNullOrWhiteSpace(apiKey)) return results;

        var voice = (await GetVoicesAsync(ct))[0];
        var models = ((await settings.GetAsync("Llm.Gemini.TtsModels", ct)) ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var model in models)
        {
            var sw = Stopwatch.StartNew();
            var (audio, _, error) = await CallInteractionsAsync(apiKey, model, voice, "Hello!", ct);
            if (audio is null && error is "HTTP_400" or "HTTP_404" or "NO_AUDIO")
                (audio, _, error) = await CallGenerateContentAsync(apiKey, model, voice, "Hello!", ct);
            sw.Stop();

            var seconds = audio is null ? 0 : EnsureWav(audio).Length / (SampleRate * 2.0);
            results.Add(new()
            {
                Provider = "Gemini", Model = model, Kind = "Ses", Success = audio is not null,
                DurationMs = (int)sw.ElapsedMilliseconds, ErrorCode = error,
                ErrorMessage = audio is null ? LlmService.DescribeError(error, null) : null,
                Sample = audio is null ? null : $"{seconds:0.0} sn ses üretildi ({voice})"
            });
        }
        return results;
    }

    // ---------- Gemini çağrıları ----------

    /// <summary>Yeni "Interactions" uç noktası (güncel dokümandaki format).</summary>
    private Task<(byte[]? Audio, int Tokens, string? Error)> CallInteractionsAsync(string key, string model, string voice, string text, CancellationToken ct) =>
        PostAsync(key, "interactions", new JsonObject
        {
            ["model"] = model,
            ["input"] = new JsonArray(new JsonObject
            {
                ["type"] = "user_input",
                ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text })
            }),
            ["response_format"] = new JsonObject { ["type"] = "audio" },
            ["generation_config"] = new JsonObject { ["speech_config"] = new JsonArray(new JsonObject { ["voice"] = voice }) }
        }, ct);

    /// <summary>Klasik generateContent formatı (eski/önizleme TTS modelleri için yedek).</summary>
    private Task<(byte[]? Audio, int Tokens, string? Error)> CallGenerateContentAsync(string key, string model, string voice, string text, CancellationToken ct) =>
        PostAsync(key, $"models/{Uri.EscapeDataString(model)}:generateContent", new JsonObject
        {
            ["contents"] = new JsonArray(new JsonObject { ["parts"] = new JsonArray(new JsonObject { ["text"] = text }) }),
            ["generationConfig"] = new JsonObject
            {
                ["responseModalities"] = new JsonArray("AUDIO"),
                ["speechConfig"] = new JsonObject
                {
                    ["voiceConfig"] = new JsonObject { ["prebuiltVoiceConfig"] = new JsonObject { ["voiceName"] = voice } }
                }
            }
        }, ct);

    private async Task<(byte[]? Audio, int Tokens, string? Error)> PostAsync(string key, string path, JsonObject body, CancellationToken ct)
    {
        var baseUrl = config["Llm:BaseUrls:GeminiNative"] is { Length: > 0 } o ? o.TrimEnd('/') + "/" : DefaultBaseUrl;
        using var request = new HttpRequestMessage(HttpMethod.Post, baseUrl + path)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json")
        };
        request.Headers.TryAddWithoutValidation("x-goog-api-key", key);

        try
        {
            using var response = await httpFactory.CreateClient(Infrastructure.Llm.OpenAiCompatibleClient.HttpClientName).SendAsync(request, ct);
            var json = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode) return (null, 0, $"HTTP_{(int)response.StatusCode}");

            var root = JsonNode.Parse(json);
            var data = FindAudioBase64(root);
            if (data is null) return (null, 0, "NO_AUDIO");

            var tokens = root?["usage"]?["total_tokens"]?.GetValue<int>()
                         ?? root?["usageMetadata"]?["totalTokenCount"]?.GetValue<int>() ?? 0;
            return (Convert.FromBase64String(data), tokens, null);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested) { return (null, 0, "TIMEOUT"); }
        catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException or FormatException or InvalidOperationException)
        {
            logger.LogWarning(ex, "Gemini TTS yanıtı okunamadı.");
            return (null, 0, "BAD_RESPONSE");
        }
    }

    /// <summary>Yanıtta "data" alanındaki base64 sesi bulur (iki formatta da farklı yerde durur).</summary>
    public static string? FindAudioBase64(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                if (obj["data"] is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 100) return s;
                foreach (var (_, child) in obj)
                    if (FindAudioBase64(child) is { } found) return found;
                break;
            case JsonArray arr:
                foreach (var child in arr)
                    if (FindAudioBase64(child) is { } found) return found;
                break;
        }
        return null;
    }

    /// <summary>Ham PCM (16-bit, 24 kHz, mono) geldiyse başına WAV başlığı ekler.</summary>
    public static byte[] EnsureWav(byte[] audio)
    {
        if (audio.Length >= 4 && audio[0] == 'R' && audio[1] == 'I' && audio[2] == 'F' && audio[3] == 'F') return audio;

        using var ms = new MemoryStream(44 + audio.Length);
        using var w = new BinaryWriter(ms);
        w.Write("RIFF"u8); w.Write(36 + audio.Length); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1);
        w.Write(SampleRate); w.Write(SampleRate * 2); w.Write((short)2); w.Write((short)16);
        w.Write("data"u8); w.Write(audio.Length); w.Write(audio);
        return ms.ToArray();
    }

    public static string CacheKey(string voice, string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(voice + "|" + text))).ToLowerInvariant()[..32];

    private async Task<List<string>> GetVoicesAsync(CancellationToken ct)
    {
        var list = ((await settings.GetAsync("Llm.Gemini.TtsVoices", ct)) ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        return list.Count > 0 ? list : ["Kore"];
    }

    private static AppException Unavailable(string message) =>
        new("TTS_UNAVAILABLE", message, StatusCodes.Status503ServiceUnavailable);
}

public sealed class TtsRequestValidator : AbstractValidator<TtsRequest>
{
    public TtsRequestValidator()
    {
        RuleFor(x => x.Text).NotEmpty().WithMessage("Metin boş olamaz.").MaximumLength(600).WithMessage("Metin en fazla 600 karakter olabilir.");
        RuleFor(x => x.Voice).MaximumLength(40);
    }
}
