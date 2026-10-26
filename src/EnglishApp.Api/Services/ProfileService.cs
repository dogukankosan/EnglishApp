using System.Globalization;
using EnglishApp.Api.Data;
using EnglishApp.Api.Infrastructure.Errors;
using EnglishApp.Api.Infrastructure.Security;
using EnglishApp.Api.Infrastructure.Time;
using EnglishApp.Api.Infrastructure.Validation;
using EnglishApp.Shared.Dtos;
using FluentValidation;

namespace EnglishApp.Api.Services;

public interface IProfileService
{
    Task<ProfileDto> GetAsync(CancellationToken ct);
    Task UpdateAsync(UpdateProfileRequest req, CancellationToken ct);
    Task<UserSettingsDto> UpdateSettingsAsync(UpdateSettingsRequest req, CancellationToken ct);
    Task<string> UploadPhotoAsync(IFormFile file, CancellationToken ct);
    Task DeletePhotoAsync(CancellationToken ct);
    Task UpdateGoalAsync(GoalRequest req, CancellationToken ct);
}

public sealed class ProfileService(
    IUserRepository users,
    IUserSettingsRepository settings,
    IFileStorage files,
    ICurrentUser currentUser,
    ITrClock clock,
    ILogger<ProfileService> logger) : IProfileService
{
    public async Task<ProfileDto> GetAsync(CancellationToken ct)
    {
        var user = await users.GetByIdAsync(currentUser.Id, ct) ?? throw AppException.NotFound("Kullanıcı bulunamadı.");
        var s = await settings.GetAsync(user.Id, ct);
        return new ProfileDto
        {
            Id = user.Id,
            Email = user.Email,
            FullName = user.FullName,
            Role = user.RoleId.ToString(),
            ProfileImage = user.ProfileImagePath,
            DailyTokenLimit = user.DailyTokenLimit,
            CreatedAt = user.CreatedAt,
            LastLoginAt = user.LastLoginAt,
            Settings = ToDto(s)
        };
    }

    public async Task UpdateAsync(UpdateProfileRequest req, CancellationToken ct)
    {
        await users.UpdateFullNameAsync(currentUser.Id, req.FullName.Trim(), clock.Now, ct);
        logger.LogInformation("Profil güncellendi.");
    }

    public async Task<UserSettingsDto> UpdateSettingsAsync(UpdateSettingsRequest req, CancellationToken ct)
    {
        var s = await settings.GetAsync(currentUser.Id, ct);
        s.DailyNewWordLimit = req.DailyNewWordLimit;
        s.DailyReviewLimit = req.DailyReviewLimit;
        s.DesiredRetention = req.DesiredRetention;
        s.ReminderEnabled = req.ReminderEnabled;
        s.ReminderTime = ParseTime(req.ReminderTime);
        s.WeeklyReportEnabled = req.WeeklyReportEnabled;
        s.WeeklyReportDay = req.WeeklyReportDay;
        s.MonthlyReportEnabled = req.MonthlyReportEnabled;
        s.TtsVoice = string.IsNullOrWhiteSpace(req.TtsVoice) ? null : req.TtsVoice.Trim();
        s.TtsRate = req.TtsRate;

        await settings.UpdateAsync(s, clock.Now, ct);
        logger.LogInformation("Kullanıcı ayarları güncellendi.");
        return ToDto(s);
    }

    public async Task<string> UploadPhotoAsync(IFormFile file, CancellationToken ct)
    {
        var user = await users.GetByIdAsync(currentUser.Id, ct) ?? throw AppException.NotFound();
        var fileName = await files.SaveProfilePhotoAsync(file, ct);

        await users.UpdatePhotoAsync(user.Id, fileName, clock.Now, ct);
        if (!string.IsNullOrEmpty(user.ProfileImagePath))
            files.DeleteProfilePhoto(user.ProfileImagePath);

        logger.LogInformation("Profil resmi güncellendi: {FileName}", fileName);
        return fileName;
    }

    public async Task DeletePhotoAsync(CancellationToken ct)
    {
        var user = await users.GetByIdAsync(currentUser.Id, ct) ?? throw AppException.NotFound();
        if (string.IsNullOrEmpty(user.ProfileImagePath)) return;

        await users.UpdatePhotoAsync(user.Id, null, clock.Now, ct);
        files.DeleteProfilePhoto(user.ProfileImagePath);
    }

    public async Task UpdateGoalAsync(GoalRequest req, CancellationToken ct)
    {
        byte? level = Enum.TryParse<EnglishApp.Shared.Enums.CefrLevel>(req.TargetLevel, true, out var l) ? (byte)l : null;
        DateTime? date = DateTime.TryParseExact(req.TargetDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
        if (level is null || date is null) { level = null; date = null; } // ikisi birlikte yoksa hedef silinir
        await settings.UpdateGoalAsync(currentUser.Id, level, date, req.StreakFreeze, clock.Now, ct);
        logger.LogInformation("Hedef güncellendi: {Level} {Date}, dinlenme günü: {Freeze}", level, date, req.StreakFreeze);
    }

    public static UserSettingsDto ToDto(UserSettingsRow s) => new()
    {
        DailyNewWordLimit = s.DailyNewWordLimit,
        DailyReviewLimit = s.DailyReviewLimit,
        DesiredRetention = s.DesiredRetention,
        ReminderEnabled = s.ReminderEnabled,
        ReminderTime = s.ReminderTime.ToString(@"hh\:mm", CultureInfo.InvariantCulture),
        WeeklyReportEnabled = s.WeeklyReportEnabled,
        WeeklyReportDay = s.WeeklyReportDay,
        MonthlyReportEnabled = s.MonthlyReportEnabled,
        TtsVoice = s.TtsVoice,
        TtsRate = s.TtsRate,
        PlacementTestDone = s.PlacementTestDone,
        TargetLevel = s.TargetLevel is null ? null : ((EnglishApp.Shared.Enums.CefrLevel)s.TargetLevel.Value).ToString(),
        TargetDate = s.TargetDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        StreakFreeze = s.StreakFreeze
    };

    public static TimeSpan ParseTime(string value) =>
        TimeSpan.ParseExact(value, @"hh\:mm", CultureInfo.InvariantCulture);
}

public sealed class UpdateProfileRequestValidator : AbstractValidator<UpdateProfileRequest>
{
    public UpdateProfileRequestValidator() => RuleFor(x => x.FullName).ValidFullName();
}

public sealed class UpdateSettingsRequestValidator : AbstractValidator<UpdateSettingsRequest>
{
    public UpdateSettingsRequestValidator()
    {
        RuleFor(x => x.DailyNewWordLimit).InclusiveBetween((short)0, (short)200).WithMessage("Günlük yeni kelime limiti 0-200 arasında olmalı.");
        RuleFor(x => x.DailyReviewLimit).InclusiveBetween((short)0, (short)2000).WithMessage("Günlük tekrar limiti 0-2000 arasında olmalı.");
        RuleFor(x => x.DesiredRetention).InclusiveBetween(0.70m, 0.99m).WithMessage("Hedef hatırlama oranı %70-%99 arasında olmalı.");
        RuleFor(x => x.ReminderTime)
            .Must(t => TimeSpan.TryParseExact(t, @"hh\:mm", CultureInfo.InvariantCulture, out _))
            .WithMessage("Hatırlatma saati SS:dd formatında olmalı.");
        RuleFor(x => x.WeeklyReportDay).InclusiveBetween((byte)1, (byte)7).WithMessage("Haftalık rapor günü geçersiz.");
        RuleFor(x => x.TtsRate).InclusiveBetween(0.5m, 2.0m).WithMessage("Konuşma hızı 0.5-2.0 arasında olmalı.");
        RuleFor(x => x.TtsVoice).MaximumLength(100);
    }
}

public sealed class GoalRequestValidator : AbstractValidator<GoalRequest>
{
    public GoalRequestValidator()
    {
        RuleFor(x => x.TargetLevel).Must(l => string.IsNullOrEmpty(l) || Enum.TryParse<EnglishApp.Shared.Enums.CefrLevel>(l, true, out _))
            .WithMessage("Geçersiz hedef seviye.");
        RuleFor(x => x.TargetDate).Must(d => string.IsNullOrEmpty(d) || DateTime.TryParseExact(d, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var t) && t > DateTime.UtcNow.Date && t < DateTime.UtcNow.AddYears(5))
            .WithMessage("Hedef tarihi bugünden sonra ve 5 yıl içinde olmalı.");
    }
}