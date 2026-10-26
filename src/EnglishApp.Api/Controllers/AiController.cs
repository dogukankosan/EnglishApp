using EnglishApp.Api.Infrastructure.Security;
using EnglishApp.Api.Services.Ai;
using EnglishApp.Shared.Common;
using EnglishApp.Shared.Dtos;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace EnglishApp.Api.Controllers;

[Route("api/v1/ai")]
public sealed class AiController(ILlmService llm, ITtsService tts, ISttService stt, ICurrentUser currentUser) : ApiControllerBase
{
    /// <summary>Kaydedilen sesi yazıya çevirir (Groq Whisper). Tarayıcının kendi tanıması çalışmazsa kullanılır.</summary>
    [HttpPost("stt")]
    [EnableRateLimiting("ai")]
    [RequestSizeLimit(GroqWhisperSttService.MaxBytes + 64 * 1024)]
    public async Task<ActionResult<ApiResponse<SttResultDto>>> Stt(IFormFile? file, CancellationToken ct)
    {
        if (file is null || file.Length == 0)
            throw new EnglishApp.Api.Infrastructure.Errors.AppException("INVALID_FILE", "Ses kaydı boş. Mikrofona biraz daha uzun konuş.");
        if (file.Length > GroqWhisperSttService.MaxBytes)
            throw new EnglishApp.Api.Infrastructure.Errors.AppException("INVALID_FILE", "Kayıt çok uzun. Tek seferde en fazla ~1 dakika konuş.");
        if (!(file.ContentType ?? "").StartsWith("audio/", StringComparison.OrdinalIgnoreCase) && !(file.ContentType ?? "").StartsWith("video/webm", StringComparison.OrdinalIgnoreCase))
            throw new EnglishApp.Api.Infrastructure.Errors.AppException("INVALID_FILE", "Geçersiz ses dosyası.");

        await using var s = file.OpenReadStream();
        return Success(await stt.TranscribeAsync(s, file.FileName, file.ContentType!, ct));
    }

    /// <summary>Bugünkü yapay zeka kullanımı ve kalan hak.</summary>
    [HttpGet("quota")]
    public async Task<ActionResult<ApiResponse<AiQuotaDto>>> Quota(CancellationToken ct) =>
        Success(await llm.GetQuotaAsync(currentUser.Id, ct));

    /// <summary>
    /// Metni Gemini'nin doğal sesiyle WAV olarak döner. Başarısızsa 503 TTS_UNAVAILABLE:
    /// istemci tarayıcının kendi sesine geçer.
    /// </summary>
    [HttpPost("tts")]
    [EnableRateLimiting("ai")]
    public async Task<IActionResult> Tts(TtsRequest req, CancellationToken ct)
    {
        var wav = await tts.SynthesizeAsync(req, ct);
        Response.Headers.CacheControl = "private, max-age=86400";
        return File(wav, "audio/wav");
    }
}

[Route("api/v1/ai/conversations")]
[EnableRateLimiting("ai")]
public sealed class ConversationsController(IConversationService service, ITtsService tts) : ApiControllerBase
{
    [HttpGet]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<ConversationListItemDto>>>> List(CancellationToken ct) =>
        Success(await service.ListAsync(ct));

    [HttpGet("{id:long}")]
    public async Task<ActionResult<ApiResponse<ConversationDto>>> Get(long id, CancellationToken ct) =>
        Success(await WithVoiceAsync(await service.GetAsync(id, ct), ct));

    [HttpPost]
    public async Task<ActionResult<ApiResponse<ConversationDto>>> Start(StartConversationRequest req, CancellationToken ct) =>
        Success(await WithVoiceAsync(await service.StartAsync(req, ct), ct));

    [HttpPost("{id:long}/messages")]
    public async Task<ActionResult<ApiResponse<SendMessageResultDto>>> Send(long id, SendMessageRequest req, CancellationToken ct) =>
        Success(await service.SendAsync(id, req, ct));

    [HttpPost("{id:long}/finish")]
    public async Task<ActionResult<ApiResponse<ConversationDto>>> Finish(long id, CancellationToken ct) =>
        Success(await WithVoiceAsync(await service.FinishAsync(id, ct), ct));

    [HttpDelete("{id:long}")]
    public async Task<ActionResult<ApiResponse>> Delete(long id, CancellationToken ct)
    {
        await service.DeleteAsync(id, ct);
        return Success();
    }

    private async Task<ConversationDto> WithVoiceAsync(ConversationDto dto, CancellationToken ct)
    {
        dto.TtsVoice = await tts.VoiceForRoleAsync(dto.AiRole, ct);
        return dto;
    }
}

[Route("api/v1/ai/writing")]
[EnableRateLimiting("ai")]
public sealed class WritingController(IWritingService service) : ApiControllerBase
{
    [HttpGet("tasks")]
    public ActionResult<ApiResponse<IReadOnlyList<WritingTaskDto>>> Tasks() => Success(service.GetTasks());

    [HttpGet]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<WritingListItemDto>>>> List(CancellationToken ct) =>
        Success(await service.ListAsync(ct));

    [HttpGet("{id:long}")]
    public async Task<ActionResult<ApiResponse<WritingResultDto>>> Get(long id, CancellationToken ct) =>
        Success(await service.GetAsync(id, ct));

    [HttpPost]
    public async Task<ActionResult<ApiResponse<WritingResultDto>>> Submit(WritingSubmitRequest req, CancellationToken ct) =>
        Success(await service.SubmitAsync(req, ct));

    [HttpDelete("{id:long}")]
    public async Task<ActionResult<ApiResponse>> Delete(long id, CancellationToken ct)
    {
        await service.DeleteAsync(id, ct);
        return Success();
    }
}
