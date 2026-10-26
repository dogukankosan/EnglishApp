using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using EnglishApp.Api.Infrastructure.Errors;
using EnglishApp.Api.Infrastructure.Llm;
using EnglishApp.Api.Infrastructure.Security;
using EnglishApp.Api.Infrastructure.Settings;
using EnglishApp.Shared.Dtos;
using EnglishApp.Shared.Enums;

namespace EnglishApp.Api.Services.Ai;

public sealed class SttResultDto
{
    public string Text { get; set; } = "";
}

/// <summary>
/// Sunucu tarafı ses tanıma: Groq Whisper (ücretsiz, hızlı).
/// Tarayıcının kendi tanıması olmayan/çalışmayan tarayıcılarda (Brave, Firefox, Safari) kullanılır;
/// Türk aksanlı İngilizceyi de tarayıcıdan daha iyi anlar.
/// </summary>
public interface ISttService
{
    Task<SttResultDto> TranscribeAsync(Stream audio, string fileName, string contentType, CancellationToken ct);
    Task<ProviderTestResultDto?> TestAsync(CancellationToken ct);
}

public sealed class GroqWhisperSttService(
    IHttpClientFactory httpFactory,
    ISettingsService settings,
    ILlmService llm,
    ICurrentUser currentUser,
    IConfiguration config,
    IWebHostEnvironment env,
    ILogger<GroqWhisperSttService> logger) : ISttService
{
    public const long MaxBytes = 5 * 1024 * 1024;

    public async Task<SttResultDto> TranscribeAsync(Stream audio, string fileName, string contentType, CancellationToken ct)
    {
        var userId = currentUser.Id;
        await llm.EnsureQuotaAsync(userId, ct);

        var (key, model) = await GetConfigAsync(ct);
        if (key is null) throw Unavailable("Sunucu ses tanıması için Groq API anahtarı girilmemiş (Admin > Ayarlar).");

        var (text, error, ms) = await CallAsync(key, model, audio, fileName, contentType, ct);

        // Groq saniye bazlı ücretlendirir; günlük sayaç için kabaca: 1 sn ses ≈ 25 token (WebM/Opus ≈ 4 KB/sn)
        var estimatedTokens = (int)Math.Max(10, audio.CanSeek ? audio.Length / 4000 * 25 : 100);
        await llm.RecordExternalUsageAsync(userId, LlmProvider.Groq, model, AiFeature.Stt, estimatedTokens, text is null ? 0 : text.Length / 4,
            text is not null, false, ms, error);

        if (text is null)
        {
            logger.LogWarning("Ses tanıma başarısız: {Model} {ErrorCode}", model, error);
            throw Unavailable("Ses tanınamadı. " + LlmService.DescribeError(error, null));
        }
        return new SttResultDto { Text = text.Trim() };
    }

    /// <summary>Hazır MP3'lerden biriyle Whisper'ı dener (admin test butonu).</summary>
    public async Task<ProviderTestResultDto?> TestAsync(CancellationToken ct)
    {
        var (key, model) = await GetConfigAsync(ct);
        if (key is null) return null;

        var sample = Directory.Exists(AudioDir) ? Directory.EnumerateFiles(AudioDir, "*.mp3").FirstOrDefault() : null;
        if (sample is null)
            return new() { Provider = "Groq", Model = model, Kind = "Ses tanıma", Success = false, ErrorMessage = "Test için örnek ses dosyası bulunamadı." };

        await using var fs = File.OpenRead(sample);
        var (text, error, ms) = await CallAsync(key, model, fs, Path.GetFileName(sample), "audio/mpeg", ct);
        return new()
        {
            Provider = "Groq", Model = model, Kind = "Ses tanıma", Success = text is not null, DurationMs = ms, ErrorCode = error,
            ErrorMessage = text is null ? LlmService.DescribeError(error, null) : null,
            Sample = text is null ? null : $"Duyduğu: {text.Trim()}"
        };
    }

    private string AudioDir => Path.Combine(env.ContentRootPath, "Content", "audio");

    private async Task<(string? Key, string Model)> GetConfigAsync(CancellationToken ct)
    {
        var key = await settings.GetAsync("Llm.Groq.ApiKey", ct);
        var model = await settings.GetAsync("Llm.Groq.SttModel", ct);
        return (string.IsNullOrWhiteSpace(key) ? null : key, string.IsNullOrWhiteSpace(model) ? "whisper-large-v3-turbo" : model);
    }

    private async Task<(string? Text, string? Error, int Ms)> CallAsync(string key, string model, Stream audio, string fileName, string contentType, CancellationToken ct)
    {
        using var form = new MultipartFormDataContent();
        var file = new StreamContent(audio);
        file.Headers.ContentType = MediaTypeHeaderValue.TryParse(contentType, out var mt) ? mt : new MediaTypeHeaderValue("audio/webm");
        form.Add(file, "file", string.IsNullOrWhiteSpace(fileName) ? "speech.webm" : fileName);
        form.Add(new StringContent(model), "model");
        form.Add(new StringContent("en"), "language");        // sadece İngilizce: Türkçe sanıp çevirmesin
        form.Add(new StringContent("json"), "response_format");
        form.Add(new StringContent("0"), "temperature");

        using var request = new HttpRequestMessage(HttpMethod.Post, OpenAiCompatibleClient.BaseUrlFor(LlmProvider.Groq, config) + "audio/transcriptions")
        {
            Content = form
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);

        var sw = Stopwatch.StartNew();
        try
        {
            using var response = await httpFactory.CreateClient(OpenAiCompatibleClient.HttpClientName).SendAsync(request, ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            sw.Stop();
            if (!response.IsSuccessStatusCode) return (null, $"HTTP_{(int)response.StatusCode}", (int)sw.ElapsedMilliseconds);
            var text = JsonNode.Parse(body)?["text"]?.GetValue<string>();
            return (text ?? "", null, (int)sw.ElapsedMilliseconds);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested) { return (null, "TIMEOUT", (int)sw.ElapsedMilliseconds); }
        catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException or InvalidOperationException)
        {
            logger.LogWarning(ex, "Groq Whisper yanıtı okunamadı.");
            return (null, ex is HttpRequestException ? "NETWORK" : "BAD_RESPONSE", (int)sw.ElapsedMilliseconds);
        }
    }

    private static AppException Unavailable(string message) => new("STT_UNAVAILABLE", message, StatusCodes.Status503ServiceUnavailable);
}
