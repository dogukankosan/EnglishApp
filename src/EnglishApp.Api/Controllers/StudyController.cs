using EnglishApp.Api.Services;
using EnglishApp.Shared.Common;
using EnglishApp.Shared.Dtos;
using EnglishApp.Shared.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace EnglishApp.Api.Controllers;

[Route("api/v1/scenarios")]
public sealed class ScenariosController(IScenarioService service) : ApiControllerBase
{
    [HttpGet]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<ScenarioListItemDto>>>> List(CancellationToken ct) =>
        Success(await service.ListAsync(ct));

    [HttpGet("{id:int}")]
    public async Task<ActionResult<ApiResponse<ScenarioDetailDto>>> Get(int id, CancellationToken ct) =>
        Success(await service.GetAsync(id, ct));
}

[Route("api/v1/study")]
public sealed class StudyController(IStudyService service) : ApiControllerBase
{
    /// <summary>Bugünkü çalışma listesi: zamanı gelen tekrarlar + günlük limit kadar yeni içerik.</summary>
    [HttpGet("session")]
    public async Task<ActionResult<ApiResponse<StudySessionDto>>> Session([FromQuery] int? scenarioId, CancellationToken ct) =>
        Success(await service.GetSessionAsync(scenarioId, ct));

    [HttpPost("answer")]
    public async Task<ActionResult<ApiResponse<AnswerResultDto>>> Answer(AnswerRequest req, CancellationToken ct) =>
        Success(await service.AnswerAsync(req, ct));
}

[Route("api/v1/stats")]
public sealed class StatsController(IStatsService service) : ApiControllerBase
{
    [HttpGet("dashboard")]
    public async Task<ActionResult<ApiResponse<DashboardDto>>> Dashboard(CancellationToken ct) =>
        Success(await service.GetDashboardAsync(ct));

    [HttpGet("words")]
    public async Task<ActionResult<ApiResponse<PagedResult<WordProgressDto>>>> Words(
        [FromQuery] WordProgressFilter filter = WordProgressFilter.All, [FromQuery] string? search = null,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 50, CancellationToken ct = default)
    {
        if (search?.Length > 100) search = search[..100];
        return Success(await service.GetWordsAsync(filter, search, page, pageSize, ct));
    }
}

[Route("api/v1/placement")]
public sealed class PlacementController(IPlacementService service) : ApiControllerBase
{
    [HttpGet("words")]
    public async Task<ActionResult<ApiResponse<List<PlacementWordDto>>>> Words([FromQuery] int count = 30, CancellationToken ct = default) =>
        Success(await service.GetWordsAsync(count, ct));

    [HttpPost("submit")]
    public async Task<ActionResult<ApiResponse<int>>> Submit(PlacementSubmitRequest req, CancellationToken ct) =>
        Success(await service.SubmitAsync(req, ct));
}

/// <summary>Dış cron servisinin çağırdığı uç. Kullanıcı girişi yok, gizli anahtar ile korunur.</summary>
[Route("api/v1/jobs")]
[AllowAnonymous]
public sealed class JobsController(IJobService jobs) : ApiControllerBase
{
    public const string KeyHeader = "X-Job-Key";

    [HttpPost("run")]
    public async Task<ActionResult<ApiResponse<JobRunResult>>> Run(CancellationToken ct)
    {
        await jobs.ValidateKeyAsync(Request.Headers[KeyHeader].ToString(), ct);
        return Success(await jobs.RunAsync(ct));
    }
}
