using EnglishApp.Api.Services;
using EnglishApp.Shared.Common;
using EnglishApp.Shared.Dtos;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace EnglishApp.Api.Controllers;

[Route("api/v1/admin/users")]
[Authorize(Roles = "Admin")]
public sealed class AdminUsersController(IAdminUserService service) : ApiControllerBase
{
    [HttpGet]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<AdminUserDto>>>> List(CancellationToken ct) =>
        Success(await service.ListAsync(ct));

    [HttpGet("{id:int}")]
    public async Task<ActionResult<ApiResponse<AdminUserDto>>> Get(int id, CancellationToken ct) =>
        Success(await service.GetAsync(id, ct));

    [HttpPost]
    public async Task<ActionResult<ApiResponse<int>>> Create(CreateUserRequest req, CancellationToken ct) =>
        Success(await service.CreateAsync(req, ct));

    [HttpPut("{id:int}")]
    public async Task<ActionResult<ApiResponse>> Update(int id, UpdateUserRequest req, CancellationToken ct)
    {
        await service.UpdateAsync(id, req, ct);
        return Success();
    }

    [HttpPost("{id:int}/activate")]
    public async Task<ActionResult<ApiResponse>> Activate(int id, CancellationToken ct)
    {
        await service.SetActiveAsync(id, true, ct);
        return Success();
    }

    [HttpPost("{id:int}/deactivate")]
    public async Task<ActionResult<ApiResponse>> Deactivate(int id, CancellationToken ct)
    {
        await service.SetActiveAsync(id, false, ct);
        return Success();
    }

    [HttpPost("{id:int}/reset-password")]
    public async Task<ActionResult<ApiResponse>> ResetPassword(int id, ResetPasswordRequest req, CancellationToken ct)
    {
        await service.ResetPasswordAsync(id, req, ct);
        return Success();
    }

    [HttpPost("{id:int}/unlock")]
    public async Task<ActionResult<ApiResponse>> Unlock(int id, CancellationToken ct)
    {
        await service.UnlockAsync(id, ct);
        return Success();
    }

    [HttpDelete("{id:int}")]
    public async Task<ActionResult<ApiResponse>> Delete(int id, CancellationToken ct)
    {
        await service.DeleteAsync(id, ct);
        return Success();
    }
}

[Route("api/v1/admin/settings")]
[Authorize(Roles = "Admin")]
public sealed class AdminSettingsController(IAdminSettingsService service) : ApiControllerBase
{
    [HttpGet]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<SettingDto>>>> List(CancellationToken ct) =>
        Success(await service.ListAsync(ct));

    /// <summary>SMTP ayarı değiştiyse önce test maili gönderilir; başarısızsa hiçbir şey kaydedilmez.</summary>
    [HttpPut]
    public async Task<ActionResult<ApiResponse<SaveSettingsResultDto>>> Update(SaveSettingsRequest req, CancellationToken ct) =>
        Success(await service.UpdateAsync(req, ct));

    /// <summary>Ayarlı tüm yapay zeka sağlayıcılarını (sohbet + ses) küçük bir istekle dener.</summary>
    [HttpPost("test-ai")]
    public async Task<ActionResult<ApiResponse<List<ProviderTestResultDto>>>> TestAi(
        [FromServices] EnglishApp.Api.Services.Ai.ILlmService llm,
        [FromServices] EnglishApp.Api.Services.Ai.ITtsService tts,
        [FromServices] EnglishApp.Api.Services.Ai.ISttService stt,
        CancellationToken ct)
    {
        var results = await llm.TestProvidersAsync(ct);
        results.AddRange(await tts.TestAsync(ct));
        if (await stt.TestAsync(ct) is { } sttResult) results.Add(sttResult);
        return Success(results);
    }

    /// <summary>Günlük / haftalık / aylık mailin örneğini işlemi yapan adminin kendi adresine gönderir.</summary>
    [HttpPost("sample-report/{type}")]
    public async Task<ActionResult<ApiResponse>> SampleReport(string type, [FromServices] IJobService jobs,
        [FromServices] EnglishApp.Api.Infrastructure.Security.ICurrentUser me, CancellationToken ct)
    {
        await jobs.SendSampleReportAsync(me.Id, type.ToLowerInvariant(), ct);
        return Success();
    }

    [HttpPost("test-mail")]
    public async Task<ActionResult<ApiResponse>> TestMail(TestMailRequest req, CancellationToken ct)
    {
        await service.SendTestMailAsync(req, ct);
        return Success();
    }
}

[Route("api/v1/admin/logs")]
[Authorize(Roles = "Admin")]
public sealed class AdminLogsController(ILogQueryService service) : ApiControllerBase
{
    [HttpGet]
    public async Task<ActionResult<ApiResponse<PagedResult<LogItemDto>>>> Query([FromQuery] LogQuery query, CancellationToken ct) =>
        Success(await service.QueryAsync(query, ct));

    /// <summary>Filtrelenmiş logları Excel dosyası olarak indirir (en fazla 20.000 satır).</summary>
    [HttpGet("export")]
    public async Task<IActionResult> Export([FromQuery] LogQuery query, CancellationToken ct)
    {
        var (content, fileName, _) = await service.ExportExcelAsync(query, ct);
        return File(content, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", fileName);
    }
}
