using Dapper;
using EnglishApp.Api.Data;
using EnglishApp.Api.Infrastructure.Data;
using EnglishApp.Api.Infrastructure.Errors;
using EnglishApp.Api.Infrastructure.Security;
using EnglishApp.Api.Infrastructure.Time;
using EnglishApp.Api.Services.Fsrs;
using EnglishApp.Shared.Common;
using EnglishApp.Shared.Dtos;
using EnglishApp.Shared.Enums;
using FluentValidation;

namespace EnglishApp.Api.Services;

public interface IStatsService
{
    Task<DashboardDto> GetDashboardAsync(CancellationToken ct);
    Task<PagedResult<WordProgressDto>> GetWordsAsync(WordProgressFilter filter, string? search, int page, int pageSize, CancellationToken ct);
    Task<(int Streak, int Best, bool StudiedToday)> GetStreakAsync(int userId, CancellationToken ct);
}

public sealed class StatsService(
    IStatsRepository stats,
    IStudyRepository study,
    IContentRepository content,
    IUserSettingsRepository settings,
    EnglishApp.Api.Infrastructure.Files.IAudioStore audio,
    IServiceProvider services,
    ICurrentUser currentUser,
    ITrClock clock) : IStatsService
{
    private static readonly FsrsScheduler Fsrs = new();

    public async Task<DashboardDto> GetDashboardAsync(CancellationToken ct)
    {
        var userId = currentUser.Id;
        var today = clock.Today;
        var weekStart = today.AddDays(-(((int)today.DayOfWeek + 6) % 7)); // Pazartesi

        var days = await stats.GetDailyStatsAsync(userId, today.AddDays(-370), today, ct);
        var byDate = days.ToDictionary(d => d.StatDate.Date);
        var counts = await stats.GetWordCountsAsync(userId, ct);
        var totalWords = await stats.GetPublishedWordCountAsync(ct);
        var s = await settings.GetAsync(userId, ct);
        var (streak, best, studiedToday) = CalculateStreak(byDate, today, s.StreakFreeze);

        var vocabulary = counts.Learned + counts.Known;
        var (level, note) = EstimateLevel(vocabulary, totalWords);

        return new DashboardDto
        {
            EstimatedLevel = level,
            EstimatedLevelNote = note,
            VocabularySize = vocabulary,
            Today = Sum(days.Where(d => d.StatDate.Date == today)),
            Week = Sum(days.Where(d => d.StatDate.Date >= weekStart)),
            Streak = streak,
            BestStreak = best,
            StudiedToday = studiedToday,
            DueNow = await study.CountDueAsync(userId, clock.Now, today.AddDays(1), ct),
            LearningWords = counts.Learning,
            LearnedWords = counts.Learned,
            StrugglingWords = counts.Struggling,
            KnownWords = counts.Known,
            PlacementTestDone = s.PlacementTestDone,
            Goal = BuildGoal(s.TargetLevel, s.TargetDate, vocabulary, days, today),
            // Dinleme testi servisi döngüsel bağımlılık olmasın diye ihtiyaç anında alınır
            Listening = await services.GetRequiredService<EnglishApp.Api.Services.Ai.IListeningQuizService>().GetSummaryAsync(userId, ct),
            Last30Days = Enumerable.Range(0, 30).Select(i => today.AddDays(i - 29)).Select(d =>
            {
                byDate.TryGetValue(d, out var r);
                return new DailyStatDto
                {
                    Date = d,
                    NewCount = r?.NewCount ?? 0,
                    ReviewCount = r?.ReviewCount ?? 0,
                    CorrectCount = r?.CorrectCount ?? 0,
                    StudyMinutes = (int)Math.Round((r?.StudySeconds ?? 0) / 60.0)
                };
            }).ToList(),
            // Isı haritası: son 26 hafta (Pazartesi'den başlayan tam haftalar)
            Heatmap = Enumerable.Range(0, 26 * 7).Select(i => weekStart.AddDays(-25 * 7 + i)).Where(d => d <= today)
                .Select(d => new HeatmapDayDto { Date = d, Count = byDate.TryGetValue(d, out var r) ? r.ReviewCount : 0 }).ToList(),
            Scenarios = (await content.ListScenariosAsync(userId, null, ct)).ToList()
        };
    }

    public async Task<PagedResult<WordProgressDto>> GetWordsAsync(WordProgressFilter filter, string? search, int page, int pageSize, CancellationToken ct)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 10, 100);
        var now = clock.Now;
        var result = await stats.GetWordProgressAsync(currentUser.Id, filter, search, page, pageSize, ct);

        var items = result.Items.Select(r => new WordProgressDto
        {
            WordId = r.WordId,
            Lemma = r.Lemma,
            MeaningTr = r.MeaningTr,
            Level = r.Level,
            Status = StatusOf(r),
            Reps = r.Reps,
            Lapses = r.Lapses,
            NextDueAt = r.DueAt,
            LastReviewAt = r.LastReviewAt,
            AudioUrl = audio.WordUrl(r.Lemma),
            AudioUrlUk = audio.WordUrl(r.Lemma, uk: true),
            RetentionPercent = r.IsKnown ? 100
                : r.Stability is > 0 && r.LastReviewAt is not null
                    ? (int)Math.Round(100 * Fsrs.Retrievability((now - r.LastReviewAt.Value).TotalDays, r.Stability.Value))
                    : null
        }).ToList();

        return new PagedResult<WordProgressDto>(items, result.TotalCount, result.Page, result.PageSize);
    }

    public async Task<(int Streak, int Best, bool StudiedToday)> GetStreakAsync(int userId, CancellationToken ct)
    {
        var today = clock.Today;
        var days = await stats.GetDailyStatsAsync(userId, today.AddDays(-370), today, ct);
        var s = await settings.GetAsync(userId, ct);
        return CalculateStreak(days.ToDictionary(d => d.StatDate.Date), today, s.StreakFreeze);
    }

    /// <summary>
    /// Seri hesabı. freeze = haftada 1 dinlenme günü: aynı hafta (Pzt-Paz) içinde kaçırılan ilk gün seriyi bozmaz
    /// (o gün seriye eklenmez ama zincir kopmaz). Haftada ikinci kaçırma seriyi bitirir.
    /// </summary>
    public static (int Streak, int Best, bool StudiedToday) CalculateStreak(IReadOnlyDictionary<DateTime, DailyStatsRow> byDate, DateTime today, bool freeze = false)
    {
        bool Studied(DateTime d) => byDate.TryGetValue(d, out var r) && r.ReviewCount > 0;
        static DateTime WeekOf(DateTime d) => d.AddDays(-(((int)d.DayOfWeek + 6) % 7)).Date;

        var studiedToday = Studied(today);
        // Bugün henüz çalışılmadıysa seri dünden sayılır (gün bitmeden bozulmuş sayılmaz)
        var cursor = studiedToday ? today : today.AddDays(-1);
        var streak = 0;
        var frozenWeeks = new HashSet<DateTime>();
        for (var guard = 0; guard < 400; guard++, cursor = cursor.AddDays(-1))
        {
            if (Studied(cursor)) { streak++; continue; }
            // Dinlenme hakkı: sadece seri devam ediyorsa ve o hafta henüz kullanılmadıysa
            var canFreeze = streak > 0 || (!studiedToday && cursor == today.AddDays(-1));
            if (freeze && canFreeze && Studied(cursor.AddDays(-1)) && frozenWeeks.Add(WeekOf(cursor))) continue;
            break;
        }

        int best = 0, run = 0;
        var usedWeeks = new HashSet<DateTime>();
        for (var d = today.AddDays(-370); d <= today; d = d.AddDays(1))
        {
            if (Studied(d)) run++;
            else if (!(freeze && run > 0 && usedWeeks.Add(WeekOf(d)) && Studied(d.AddDays(1)))) run = 0;
            best = Math.Max(best, run);
        }
        return (streak, Math.Max(best, streak), studiedToday);
    }

    /// <summary>Seviye hedefine ulaşmak için gereken kelime dağarcığı (EstimateLevel eşikleriyle uyumlu).</summary>
    public static int VocabularyFor(CefrLevel level) => level switch
    {
        CefrLevel.A1 => 250, CefrLevel.A2 => 500, CefrLevel.B1 => 1000, CefrLevel.B2 => 2500, CefrLevel.C1 => 4000, _ => 6000
    };

    /// <summary>Hedef planı: kalan gün, gereken günlük kelime, son 14 günün hızına göre öndesin/gerisindesin.</summary>
    public static GoalDto BuildGoal(byte? targetLevel, DateTime? targetDate, int vocabulary, IEnumerable<DailyStatsRow> days, DateTime today)
    {
        if (targetLevel is null || targetDate is null) return new GoalDto { HasGoal = false };
        var level = (CefrLevel)targetLevel.Value;
        var target = VocabularyFor(level);
        var needed = Math.Max(0, target - vocabulary);
        var daysLeft = Math.Max(0, (targetDate.Value.Date - today).Days);
        // Son 14 günde açılan yeni kelimelerin ~%85'i kalıcı öğrenilir varsayımı
        var recent = days.Where(d => d.StatDate.Date > today.AddDays(-14)).Sum(d => d.NewCount);
        var pace = Math.Round(recent / 14.0 * 0.85, 1);
        var perDay = daysLeft == 0 ? needed : Math.Round((double)needed / daysLeft, 1);

        int ahead;
        string status;
        if (needed == 0) { ahead = daysLeft; status = $"Tebrikler! {level} hedefine ulaştın. Yeni bir hedef koyabilirsin 🎉"; }
        else if (daysLeft == 0) { ahead = 0; status = "Hedef tarihine geldin. Tarihi uzat ya da hedefi güncelle."; }
        else if (pace <= 0) { ahead = -daysLeft; status = $"Son 2 haftada yeni kelime çalışmadın. Hedefe yetişmek için günde {Math.Ceiling(perDay)} kelime gerekiyor."; }
        else
        {
            var daysToFinish = (int)Math.Ceiling(needed / pace);
            ahead = daysLeft - daysToFinish;
            status = ahead >= 0
                ? $"Plana göre {ahead} gün öndesin. Böyle devam! 💪"
                : $"Plana göre {-ahead} gün gerisindesin. Günde ~{Math.Ceiling(perDay)} yeni kelimeye çık.";
        }

        return new GoalDto
        {
            HasGoal = true, TargetLevel = level.ToString(), TargetDate = targetDate, DaysLeft = daysLeft,
            CurrentVocabulary = vocabulary, TargetVocabulary = target, WordsNeeded = needed,
            WordsPerDayNeeded = perDay, CurrentPacePerDay = pace, AheadDays = ahead, StatusTr = status,
            ProgressPercent = target == 0 ? 0 : Math.Min(100, (int)Math.Round(100.0 * vocabulary / target))
        };
    }

    /// <summary>Kelime dağarcığına göre kaba CEFR tahmini (araştırmalardaki yaklaşık eşikler).</summary>
    public static (string Level, string Note) EstimateLevel(int vocabulary, int totalWordsInApp)
    {
        var level = vocabulary switch
        {
            < 250 => "A1",
            < 500 => "A1+",
            < 1000 => "A2",
            < 1500 => "B1",
            < 2500 => "B1+",
            < 4000 => "B2",
            _ => "C1"
        };
        var note = $"Öğrendiğin {vocabulary} kelimeye göre tahmin (uygulamada şu an {totalWordsInApp} kelime var).";
        return (level, note);
    }

    private static string StatusOf(WordProgressRow r) =>
        r.IsKnown ? "Biliyorum"
        : r.IsLeech ? "Zorlanıyorum"
        : r.State == (byte)CardState.Review && r.Stability >= StudyConstants.LearnedStabilityDays ? "Öğrendim"
        : "Öğreniyorum";

    private static PeriodStatsDto Sum(IEnumerable<DailyStatsRow> rows)
    {
        var list = rows.ToList();
        var review = list.Sum(r => r.ReviewCount);
        var correct = list.Sum(r => r.CorrectCount);
        return new PeriodStatsDto
        {
            NewCount = list.Sum(r => r.NewCount),
            ReviewCount = review,
            CorrectCount = correct,
            AccuracyPercent = review == 0 ? 0 : (int)Math.Round(100.0 * correct / review),
            StudyMinutes = (int)Math.Round(list.Sum(r => r.StudySeconds) / 60.0)
        };
    }
}

// ---------- Seviye testi ----------

public interface IPlacementService
{
    Task<List<PlacementWordDto>> GetWordsAsync(int count, CancellationToken ct);
    Task<int> SubmitAsync(PlacementSubmitRequest req, CancellationToken ct);
}

public sealed class PlacementService(
    IDbConnectionFactory db,
    IUserSettingsRepository settings,
    ICurrentUser currentUser,
    ITrClock clock,
    ILogger<PlacementService> logger) : IPlacementService
{
    public async Task<List<PlacementWordDto>> GetWordsAsync(int count, CancellationToken ct)
    {
        count = Math.Clamp(count, 5, 50);
        await using var c = await db.OpenAsync(ct);
        // Henüz kartı olmayan ve "biliyorum" denmemiş kelimeler; sık olandan seyrek olana
        return (await c.QueryAsync<PlacementWordDto>(new CommandDefinition(
            """
            SELECT TOP (@count) w.Id AS WordId, w.Lemma, w.MeaningTr, w.Level, w.Ipa
            FROM content.Words w
            WHERE w.Status = 1 AND w.OwnerUserId IS NULL
              AND NOT EXISTS (SELECT 1 FROM study.Cards c WHERE c.UserId = @userId AND c.CardType = 1 AND c.WordId = w.Id)
              AND NOT EXISTS (SELECT 1 FROM study.UserKnownWords k WHERE k.UserId = @userId AND k.WordId = w.Id)
            ORDER BY w.Level, CASE WHEN w.FrequencyRank IS NULL THEN 1 ELSE 0 END, w.FrequencyRank, w.Id
            """, new { count, userId = currentUser.Id }, cancellationToken: ct))).ToList();
    }

    public async Task<int> SubmitAsync(PlacementSubmitRequest req, CancellationToken ct)
    {
        var userId = currentUser.Id;
        var now = clock.Now;
        var ids = req.KnownWordIds.Distinct().ToArray();
        var inserted = 0;

        if (ids.Length > 0)
        {
            await using var c = await db.OpenAsync(ct);
            // Sadece gerçekten var olan ve daha önce eklenmemiş kelimeler
            inserted = await c.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO study.UserKnownWords (UserId, WordId, Source, MarkedAt)
                SELECT @userId, w.Id, @source, @now
                FROM content.Words w
                WHERE w.Id IN @ids AND w.Status = 1
                  AND NOT EXISTS (SELECT 1 FROM study.UserKnownWords k WHERE k.UserId = @userId AND k.WordId = w.Id)
                """, new { userId, ids, source = (byte)KnownWordSource.PlacementTest, now }, cancellationToken: ct));
        }

        if (req.Finish) await settings.SetPlacementDoneAsync(userId, now, ct);
        logger.LogInformation("Seviye testi: {Count} kelime 'biliyorum' olarak işaretlendi. Bitti: {Finish}", inserted, req.Finish);
        return inserted;
    }
}

public sealed class PlacementSubmitRequestValidator : AbstractValidator<PlacementSubmitRequest>
{
    public PlacementSubmitRequestValidator() =>
        RuleFor(x => x.KnownWordIds).Must(l => l.Count <= 200).WithMessage("Tek seferde en fazla 200 kelime gönderilebilir.");
}
