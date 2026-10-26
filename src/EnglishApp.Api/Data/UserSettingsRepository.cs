using Dapper;
using EnglishApp.Api.Infrastructure.Data;

namespace EnglishApp.Api.Data;

public sealed class UserSettingsRow
{
    public int UserId { get; set; }
    public short DailyNewWordLimit { get; set; }
    public short DailyReviewLimit { get; set; }
    public decimal DesiredRetention { get; set; }
    public bool ReminderEnabled { get; set; }
    public TimeSpan ReminderTime { get; set; }
    public bool WeeklyReportEnabled { get; set; }
    public byte WeeklyReportDay { get; set; }
    public DateTime? LastReminderSentDate { get; set; }
    public DateTime? LastWeeklyReportSentDate { get; set; }
    public string? TtsVoice { get; set; }
    public decimal TtsRate { get; set; }
    public bool PlacementTestDone { get; set; }
    public byte? TargetLevel { get; set; }
    public DateTime? TargetDate { get; set; }
    public bool StreakFreeze { get; set; } = true;
}

public interface IUserSettingsRepository
{
    Task<UserSettingsRow> GetAsync(int userId, CancellationToken ct = default);
    Task UpdateAsync(UserSettingsRow s, DateTime now, CancellationToken ct = default);
    Task SetPlacementDoneAsync(int userId, DateTime now, CancellationToken ct = default);
    Task UpdateGoalAsync(int userId, byte? targetLevel, DateTime? targetDate, bool streakFreeze, DateTime now, CancellationToken ct = default);
}

public sealed class UserSettingsRepository(IDbConnectionFactory db) : IUserSettingsRepository
{
    public async Task<UserSettingsRow> GetAsync(int userId, CancellationToken ct = default)
    {
        await using var c = await db.OpenAsync(ct);
        var row = await c.QuerySingleOrDefaultAsync<UserSettingsRow>(new CommandDefinition(
            """
            SELECT UserId, DailyNewWordLimit, DailyReviewLimit, DesiredRetention, ReminderEnabled, ReminderTime,
                   WeeklyReportEnabled, WeeklyReportDay, LastReminderSentDate, LastWeeklyReportSentDate,
                   TtsVoice, TtsRate, PlacementTestDone, TargetLevel, TargetDate, StreakFreeze
            FROM study.UserSettings WHERE UserId = @userId
            """, new { userId }, cancellationToken: ct));

        if (row is not null) return row;

        // Eski/eksik kayıt: varsayılanlarla oluştur
        await c.ExecuteAsync(new CommandDefinition(
            """
            IF NOT EXISTS (SELECT 1 FROM study.UserSettings WHERE UserId = @userId)
                INSERT INTO study.UserSettings (UserId) VALUES (@userId)
            """, new { userId }, cancellationToken: ct));
        return await GetAsync(userId, ct);
    }

    public async Task UpdateAsync(UserSettingsRow s, DateTime now, CancellationToken ct = default)
    {
        await using var c = await db.OpenAsync(ct);
        await c.ExecuteAsync(new CommandDefinition(
            """
            UPDATE study.UserSettings
            SET DailyNewWordLimit = @DailyNewWordLimit, DailyReviewLimit = @DailyReviewLimit,
                DesiredRetention = @DesiredRetention, ReminderEnabled = @ReminderEnabled, ReminderTime = @ReminderTime,
                WeeklyReportEnabled = @WeeklyReportEnabled, WeeklyReportDay = @WeeklyReportDay,
                TtsVoice = @TtsVoice, TtsRate = @TtsRate, UpdatedAt = @now
            WHERE UserId = @UserId
            """,
            new
            {
                s.UserId, s.DailyNewWordLimit, s.DailyReviewLimit, s.DesiredRetention, s.ReminderEnabled, s.ReminderTime,
                s.WeeklyReportEnabled, s.WeeklyReportDay, s.TtsVoice, s.TtsRate, now
            }, cancellationToken: ct));
    }

    public async Task UpdateGoalAsync(int userId, byte? targetLevel, DateTime? targetDate, bool streakFreeze, DateTime now, CancellationToken ct = default)
    {
        await using var c = await db.OpenAsync(ct);
        await c.ExecuteAsync(new CommandDefinition(
            "UPDATE study.UserSettings SET TargetLevel = @targetLevel, TargetDate = @targetDate, StreakFreeze = @streakFreeze, UpdatedAt = @now WHERE UserId = @userId",
            new { userId, targetLevel, targetDate, streakFreeze, now }, cancellationToken: ct));
    }

    public async Task SetPlacementDoneAsync(int userId, DateTime now, CancellationToken ct = default)
    {
        await using var c = await db.OpenAsync(ct);
        await c.ExecuteAsync(new CommandDefinition(
            "UPDATE study.UserSettings SET PlacementTestDone = 1, UpdatedAt = @now WHERE UserId = @userId",
            new { userId, now }, cancellationToken: ct));
    }
}
