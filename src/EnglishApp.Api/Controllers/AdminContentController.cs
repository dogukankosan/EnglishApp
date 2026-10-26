using EnglishApp.Api.Services.Ai;
using EnglishApp.Shared.Common;
using EnglishApp.Shared.Dtos;
using EnglishApp.Shared.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace EnglishApp.Api.Controllers;

[Route("api/v1/admin/content")]
[Authorize(Roles = "Admin")]
public sealed class AdminContentController(IAdminContentService service) : ApiControllerBase
{
    [HttpGet("categories")]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<ScenarioCategoryDto>>>> Categories(CancellationToken ct) =>
        Success(await service.CategoriesAsync(ct));

    [HttpGet("scenarios")]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<AdminScenarioDto>>>> List(CancellationToken ct) =>
        Success(await service.ListAsync(ct));

    [HttpGet("scenarios/{id:int}")]
    public async Task<ActionResult<ApiResponse<AdminScenarioDetailDto>>> Get(int id, CancellationToken ct) =>
        Success(await service.GetAsync(id, ct));

    /// <summary>AI ile yeni senaryo üretir (taslak olarak). Yayına almak için publish çağrılır.</summary>
    [HttpPost("scenarios/generate")]
    [EnableRateLimiting("ai")]
    public async Task<ActionResult<ApiResponse<int>>> Generate(GenerateScenarioRequest req, CancellationToken ct) =>
        Success(await service.GenerateAsync(req, ct));

    [HttpPost("scenarios/{id:int}/publish")]
    public async Task<ActionResult<ApiResponse>> Publish(int id, CancellationToken ct)
    {
        await service.SetStatusAsync(id, ContentStatus.Published, ct);
        return Success();
    }

    [HttpPost("scenarios/{id:int}/archive")]
    public async Task<ActionResult<ApiResponse>> Archive(int id, CancellationToken ct)
    {
        await service.SetStatusAsync(id, ContentStatus.Archived, ct);
        return Success();
    }

    [HttpDelete("scenarios/{id:int}")]
    public async Task<ActionResult<ApiResponse>> Delete(int id, CancellationToken ct)
    {
        await service.DeleteAsync(id, ct);
        return Success();
    }

    [HttpGet("words/stats")]
    public async Task<ActionResult<ApiResponse<WordPoolStatsDto>>> WordStats(CancellationToken ct) =>
        Success(await service.WordStatsAsync(ct));

    /// <summary>Sıklık sırasına göre kelime listesi (NGSL vb.) içe aktarır. Anlamlar sonra AI ile doldurulur.</summary>
    [HttpPost("words/import")]
    [RequestSizeLimit(512 * 1024)]
    public async Task<ActionResult<ApiResponse<WordImportResultDto>>> Import(WordImportRequest req, CancellationToken ct) =>
        Success(await service.ImportWordsAsync(req, ct));

    [HttpPost("words/enrich")]
    [EnableRateLimiting("ai")]
    public async Task<ActionResult<ApiResponse<EnrichResultDto>>> Enrich([FromQuery] int batches = 2, CancellationToken ct = default) =>
        Success(await service.EnrichWordsAsync(batches, ct));

    /// <summary>Kokoro ses üreticisi (tools/audiogen --manifest) için eksik ses listesi.</summary>
    [HttpGet("audio-manifest")]
    public async Task<ActionResult<ApiResponse<List<AudioManifestItemDto>>>> AudioManifest([FromQuery] bool onlyMissing = true, CancellationToken ct = default) =>
        Success(await service.AudioManifestAsync(onlyMissing, ct));
}
