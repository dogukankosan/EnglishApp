using EnglishApp.Api.Infrastructure.Errors;
using EnglishApp.Api.Infrastructure.Security;
using EnglishApp.Api.Services;
using EnglishApp.Shared.Common;
using EnglishApp.Shared.Dtos;
using Microsoft.AspNetCore.Mvc;

namespace EnglishApp.Api.Controllers;

[Route("api/v1/profile")]
public sealed class ProfileController(IProfileService profile) : ApiControllerBase
{
    [HttpGet]
    public async Task<ActionResult<ApiResponse<ProfileDto>>> Get(CancellationToken ct) =>
        Success(await profile.GetAsync(ct));

    [HttpPut]
    public async Task<ActionResult<ApiResponse>> Update(UpdateProfileRequest req, CancellationToken ct)
    {
        await profile.UpdateAsync(req, ct);
        return Success();
    }

    [HttpPut("settings")]
    public async Task<ActionResult<ApiResponse<UserSettingsDto>>> UpdateSettings(UpdateSettingsRequest req, CancellationToken ct) =>
        Success(await profile.UpdateSettingsAsync(req, ct));

    [HttpPut("goal")]
    public async Task<ActionResult<ApiResponse>> UpdateGoal(GoalRequest req, CancellationToken ct)
    {
        await profile.UpdateGoalAsync(req, ct);
        return Success();
    }

    [HttpPost("photo")]
    [RequestSizeLimit(3 * 1024 * 1024)]
    public async Task<ActionResult<ApiResponse<string>>> UploadPhoto(IFormFile? file, CancellationToken ct)
    {
        if (file is null) throw new AppException(ErrorCodes.InvalidFile, "Dosya seçilmedi.");
        return Success(await profile.UploadPhotoAsync(file, ct));
    }

    [HttpDelete("photo")]
    public async Task<ActionResult<ApiResponse>> DeletePhoto(CancellationToken ct)
    {
        await profile.DeletePhotoAsync(ct);
        return Success();
    }
}

[Route("api/v1/files")]
public sealed class FilesController(IFileStorage files, EnglishApp.Api.Infrastructure.Files.IAudioStore audio) : ApiControllerBase
{
    /// <summary>Hazır (Kokoro) ses dosyası. İçerik değişmez: tarayıcı 30 gün önbellekte tutar.</summary>
    [HttpGet("audio/{fileName}")]
    [ResponseCache(Duration = 2592000, Location = ResponseCacheLocation.Client)]
    public IActionResult Audio(string fileName)
    {
        var path = audio.GetPath(fileName);
        return path is null ? NotFound() : PhysicalFile(path, "audio/mpeg", enableRangeProcessing: true);
    }

    /// <summary>Profil resmi. Giriş yapmış herkes görebilir (isimler tahmin edilemez GUID).</summary>
    [HttpGet("profile/{fileName}")]
    [AllowWhenPasswordChangeRequired]
    [ResponseCache(Duration = 86400, Location = ResponseCacheLocation.Client)]
    public IActionResult ProfilePhoto(string fileName)
    {
        var path = files.GetProfilePhotoPath(fileName);
        if (path is null) return NotFound();

        var contentType = Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".webp" => "image/webp",
            _ => "image/jpeg"
        };
        return PhysicalFile(path, contentType);
    }
}
