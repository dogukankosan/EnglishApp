using EnglishApp.Api.Services.Ai;
using EnglishApp.Shared.Common;
using EnglishApp.Shared.Dtos;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace EnglishApp.Api.Controllers;

[Route("api/v1/listening")]
public sealed class ListeningController(IListeningQuizService service) : ApiControllerBase
{
    [HttpGet]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<ListeningScenarioDto>>>> List(CancellationToken ct) =>
        Success(await service.ListAsync(ct));

    /// <summary>Testi getirir; sorular ilk açılışta AI ile bir kez üretilir.</summary>
    [HttpGet("{scenarioId:int}")]
    [EnableRateLimiting("ai")]
    public async Task<ActionResult<ApiResponse<ListeningQuizDto>>> Get(int scenarioId, CancellationToken ct) =>
        Success(await service.GetAsync(scenarioId, ct));

    [HttpPost("submit")]
    public async Task<ActionResult<ApiResponse<QuizResultDto>>> Submit(QuizSubmitRequest req, CancellationToken ct) =>
        Success(await service.SubmitAsync(req, ct));
}

[Route("api/v1/capture")]
public sealed class CaptureController(ICaptureService service) : ApiControllerBase
{
    [HttpPost("analyze")]
    [EnableRateLimiting("ai")]
    public async Task<ActionResult<ApiResponse<CaptureAnalysisDto>>> Analyze(CaptureAnalyzeRequest req, CancellationToken ct) =>
        Success(await service.AnalyzeAsync(req, ct));

    [HttpPost]
    public async Task<ActionResult<ApiResponse<CaptureSaveResultDto>>> Save(CaptureSaveRequest req, CancellationToken ct) =>
        Success(await service.SaveAsync(req, ct));

    [HttpGet]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<CapturedSentenceDto>>>> List(CancellationToken ct) =>
        Success(await service.ListAsync(ct));

    [HttpDelete("{sentenceId:int}")]
    public async Task<ActionResult<ApiResponse>> Delete(int sentenceId, CancellationToken ct)
    {
        await service.DeleteAsync(sentenceId, ct);
        return Success();
    }
}

[Route("api/v1/travel-kits")]
public sealed class TravelKitsController(ITravelKitService service) : ApiControllerBase
{
    [HttpGet]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<TravelKitListItemDto>>>> List(CancellationToken ct) =>
        Success(await service.ListAsync(ct));

    [HttpGet("{id:long}")]
    public async Task<ActionResult<ApiResponse<TravelKitDto>>> Get(long id, CancellationToken ct) =>
        Success(await service.GetAsync(id, ct));

    [HttpPost]
    [EnableRateLimiting("ai")]
    public async Task<ActionResult<ApiResponse<TravelKitDto>>> Create(TravelKitRequest req, CancellationToken ct) =>
        Success(await service.CreateAsync(req, ct));

    [HttpPost("{id:long}/add-to-cards")]
    public async Task<ActionResult<ApiResponse<int>>> AddToCards(long id, CancellationToken ct) =>
        Success(await service.AddToCardsAsync(id, ct));

    [HttpDelete("{id:long}")]
    public async Task<ActionResult<ApiResponse>> Delete(long id, CancellationToken ct)
    {
        await service.DeleteAsync(id, ct);
        return Success();
    }
}
