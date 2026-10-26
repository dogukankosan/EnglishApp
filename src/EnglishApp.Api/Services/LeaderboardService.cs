using Dapper;
using EnglishApp.Api.Data;
using EnglishApp.Api.Infrastructure.Data;
using EnglishApp.Api.Infrastructure.Errors;
using EnglishApp.Api.Infrastructure.Security;
using EnglishApp.Api.Infrastructure.Time;
using EnglishApp.Shared.Dtos;
using EnglishApp.Shared.Enums;

namespace EnglishApp.Api.Services;

/// <summary>
/// Sıralama tablosu: tüm aktif kullanıcılar (admin dahil) birbirinin genel durumunu görür.
/// Sadece özet sayılar paylaşılır; e-posta, kelime listesi, sohbet/yazı içeriği paylaşılmaz.
/// </summary>
public interface ILeaderboardService
{
    Task<LeaderboardDto> GetAsync(string? period, CancellationToken ct);
}

public sealed class LeaderboardService(
    IDbConnectionFactory db,
    IStatsRepository stats,
    ICurrentUser currentUser,
    ITrClock clock) : ILeaderboardService
{
    public sealed class UserRow
    {
        public int Id { get; set; }
        public string FullName { get; set; } = "";
        public string? ProfileImagePath { get; set; }
        public byte RoleId { get; set; }
        public bool StreakFreeze { get; set; } = true;
        public byte? TargetLevel { get; set; }
        public int Vocabulary { get; set; }
    }

    public sealed class StatRow
    {
        public int UserId { get; set; }
        public DateTime StatDate { get; set; }
        public short NewCount { get; set; }
        public short ReviewCount { get; set; }
        public short CorrectCount { get; set; }
        public int StudySeconds { get; set; }
    }

    public async Task<LeaderboardDto> GetAsync(string? period, CancellationToken ct)
    {
        var today = clock.Today;
        var (key, from, label) = Range(period, today);

        await using var c = await db.OpenAsync(ct);
        // Kelime dağarcığı = öğrenilen (Review + sağlam) + "zaten biliyorum" (Durumum ekranıyla aynı tanım)
        var users = (await c.QueryAsync<UserRow>(new CommandDefinition(
            """
            SELECT u.Id, u.FullName, u.ProfileImagePath, u.RoleId,
                   ISNULL(s.StreakFreeze, 1) AS StreakFreeze, s.TargetLevel,
                   (SELECT COUNT(*) FROM study.Cards cd
                     WHERE cd.UserId = u.Id AND cd.CardType = 1 AND cd.WordId IS NOT NULL AND cd.IsLeech = 0
                       AND cd.State = 2 AND cd.Stability >= @learned)
                 + (SELECT COUNT(*) FROM study.UserKnownWords k
                     WHERE k.UserId = u.Id
                       AND NOT EXISTS (SELECT 1 FROM study.Cards x WHERE x.UserId = u.Id AND x.CardType = 1 AND x.WordId = k.WordId)) AS Vocabulary
            FROM auth.Users u
            LEFT JOIN study.UserSettings s ON s.UserId = u.Id
            WHERE u.IsActive = 1 AND u.IsDeleted = 0
            """, new { learned = StudyConstants.LearnedStabilityDays }, cancellationToken: ct))).ToList();

        // Seri hesabı için son ~1 yıl (kullanıcı sayısı az: tek sorgu yeterli)
        var rows = (await c.QueryAsync<StatRow>(new CommandDefinition(
            """
            SELECT UserId, StatDate, NewCount, ReviewCount, CorrectCount, StudySeconds
            FROM study.DailyStats WHERE StatDate BETWEEN @from AND @today
            """, new { from = today.AddDays(-370), today }, cancellationToken: ct))).ToList();

        var totalWords = await stats.GetPublishedWordCountAsync(ct);
        return new LeaderboardDto
        {
            Period = key, PeriodLabel = label, From = from, To = today,
            Items = BuildRows(users, rows, from, today, currentUser.Id, totalWords)
        };
    }

    /// <summary>Dönem: bugün, bu hafta (Pazartesi'den), bu ay (ayın 1'inden).</summary>
    public static (string Key, DateTime From, string Label) Range(string? period, DateTime today) => (period ?? "week").ToLowerInvariant() switch
    {
        "today" => ("today", today, "Bugün"),
        "week" => ("week", today.AddDays(-(((int)today.DayOfWeek + 6) % 7)), "Bu hafta"),
        "month" => ("month", new DateTime(today.Year, today.Month, 1), "Bu ay"),
        _ => throw new AppException(ErrorCodes.ValidationError, "Geçersiz dönem. today, week veya month olmalı.")
    };

    public static List<LeaderboardRowDto> BuildRows(IReadOnlyList<UserRow> users, IReadOnlyList<StatRow> stats,
        DateTime from, DateTime today, int meId, int totalWords)
    {
        var byUser = stats.GroupBy(s => s.UserId).ToDictionary(g => g.Key, g => g.ToList());

        var list = users.Select(u =>
        {
            var days = byUser.GetValueOrDefault(u.Id) ?? [];
            var period = days.Where(d => d.StatDate.Date >= from && d.StatDate.Date <= today).ToList();
            var streakInput = days.ToDictionary(d => d.StatDate.Date, d => new DailyStatsRow { StatDate = d.StatDate, ReviewCount = d.ReviewCount });
            var (streak, _, studiedToday) = StatsService.CalculateStreak(streakInput, today, u.StreakFreeze);

            var reviews = period.Sum(d => d.ReviewCount);
            var correct = period.Sum(d => d.CorrectCount);
            var newWords = period.Sum(d => d.NewCount);
            return new LeaderboardRowDto
            {
                FullName = u.FullName,
                ProfileImage = u.ProfileImagePath,
                IsMe = u.Id == meId,
                IsAdmin = u.RoleId == (byte)UserRole.Admin,
                EstimatedLevel = StatsService.EstimateLevel(u.Vocabulary, totalWords).Level,
                TargetLevel = u.TargetLevel is null ? null : ((CefrLevel)u.TargetLevel.Value).ToString(),
                Vocabulary = u.Vocabulary,
                Streak = streak,
                StudiedToday = studiedToday,
                LastStudyDate = days.Where(d => d.ReviewCount > 0).Select(d => (DateTime?)d.StatDate.Date).Max(),
                Reviews = reviews,
                NewWords = newWords,
                Minutes = (int)Math.Round(period.Sum(d => d.StudySeconds) / 60.0),
                ActiveDays = period.Count(d => d.ReviewCount > 0),
                AccuracyPercent = reviews == 0 ? null : (int)Math.Round(100.0 * correct / reviews),
                Points = reviews + 2 * newWords
            };
        })
        .OrderByDescending(r => r.Points).ThenByDescending(r => r.Minutes).ThenByDescending(r => r.Vocabulary)
        .ThenBy(r => r.FullName, StringComparer.Create(new System.Globalization.CultureInfo("tr-TR"), true))
        .ToList();

        // Eşit puanlılar aynı sırayı paylaşır (1, 2, 2, 4 ...)
        foreach (var r in list) r.Rank = 1 + list.Count(x => x.Points > r.Points);
        return list;
    }
}
