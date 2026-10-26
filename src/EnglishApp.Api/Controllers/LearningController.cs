using EnglishApp.Api.Services;
using EnglishApp.Api.Services.Ai;
using EnglishApp.Shared.Common;
using EnglishApp.Shared.Dtos;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace EnglishApp.Api.Controllers;

[Route("api/v1/dictation")]
public sealed class DictationController(IDictationService service) : ApiControllerBase
{
    /// <summary>Dikte cümleleri. weak=true değilse karışık; zayıf cümleler (puan &lt; 80) her zaman önce gelir.</summary>
    [HttpGet("next")]
    public async Task<ActionResult<ApiResponse<List<DictationItemDto>>>> Next([FromQuery] int? scenarioId, [FromQuery] int count = 10, CancellationToken ct = default) =>
        Success(await service.NextAsync(scenarioId, Math.Clamp(count, 1, 20), ct));

    [HttpPost("check")]
    public async Task<ActionResult<ApiResponse<DictationResultDto>>> Check(DictationCheckRequest req, CancellationToken ct) =>
        Success(await service.CheckAsync(req, ct));

    [HttpGet("stats")]
    public async Task<ActionResult<ApiResponse<DictationStatsDto>>> Stats(CancellationToken ct) =>
        Success(await service.StatsAsync(ct));
}

[Route("api/v1/coach")]
public sealed class CoachController(ICoachService service) : ApiControllerBase
{
    [HttpGet("weekly")]
    public async Task<ActionResult<ApiResponse<CoachReportDto?>>> Get(CancellationToken ct) =>
        Success(await service.GetLatestAsync(ct));

    [HttpPost("weekly")]
    [EnableRateLimiting("ai")]
    public async Task<ActionResult<ApiResponse<CoachReportDto>>> Generate(CancellationToken ct) =>
        Success(await service.GenerateAsync(ct));
}

[Route("api/v1/reading")]
public sealed class ReadingController(IReadingService service) : ApiControllerBase
{
    [HttpGet]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<ReadingListItemDto>>>> List(CancellationToken ct) =>
        Success(await service.ListAsync(ct));

    [HttpGet("{id:long}")]
    public async Task<ActionResult<ApiResponse<ReadingDto>>> Get(long id, CancellationToken ct) =>
        Success(await service.GetAsync(id, ct));

    [HttpPost]
    [EnableRateLimiting("ai")]
    public async Task<ActionResult<ApiResponse<ReadingDto>>> Create(ReadingRequest req, CancellationToken ct) =>
        Success(await service.CreateAsync(req, ct));

    [HttpPost("{id:long}/read")]
    public async Task<ActionResult<ApiResponse>> MarkRead(long id, CancellationToken ct)
    {
        await service.MarkReadAsync(id, ct);
        return Success();
    }

    [HttpDelete("{id:long}")]
    public async Task<ActionResult<ApiResponse>> Delete(long id, CancellationToken ct)
    {
        await service.DeleteAsync(id, ct);
        return Success();
    }

    /// <summary>Dokun-çevir: önce sözlük, yoksa hızlı AI.</summary>
    [HttpPost("lookup")]
    [EnableRateLimiting("ai")]
    public async Task<ActionResult<ApiResponse<WordLookupDto>>> Lookup(WordLookupRequest req, CancellationToken ct) =>
        Success(await service.LookupAsync(req, ct));

    [HttpPost("add-word")]
    public async Task<ActionResult<ApiResponse<CaptureSaveResultDto>>> AddWord(AddWordToCardsRequest req, CancellationToken ct) =>
        Success(await service.AddWordAsync(req, ct));
}

[Route("api/v1/media")]
public sealed class MediaController(IMediaService service) : ApiControllerBase
{
    [HttpGet]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<MediaItemDto>>>> List(CancellationToken ct) =>
        Success(await service.ListAsync(ct));

    [HttpPut("{id:int}/status")]
    public async Task<ActionResult<ApiResponse>> SetStatus(int id, MediaStatusRequest req, CancellationToken ct)
    {
        await service.SetStatusAsync(id, req.Status, ct);
        return Success();
    }
}

[Route("api/v1/videos")]
public sealed class VideosController(IVideoService service) : ApiControllerBase
{
    [HttpGet]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<VideoListItemDto>>>> List(CancellationToken ct) =>
        Success(await service.ListAsync(ct));

    [HttpGet("{id:long}")]
    public async Task<ActionResult<ApiResponse<VideoDto>>> Get(long id, CancellationToken ct) =>
        Success(await service.GetAsync(id, ct));

    [HttpPost]
    public async Task<ActionResult<ApiResponse<VideoDto>>> Create(VideoCreateRequest req, CancellationToken ct) =>
        Success(await service.CreateAsync(req, ct));

    [HttpDelete("{id:long}")]
    public async Task<ActionResult<ApiResponse>> Delete(long id, CancellationToken ct)
    {
        await service.DeleteAsync(id, ct);
        return Success();
    }
}