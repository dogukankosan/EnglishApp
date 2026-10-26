using System.Text;
using System.Text.Json;
using Dapper;
using EnglishApp.Api.Data;
using EnglishApp.Api.Infrastructure;
using EnglishApp.Api.Infrastructure.Data;
using EnglishApp.Api.Infrastructure.Llm;
using EnglishApp.Api.Infrastructure.Security;
using EnglishApp.Api.Infrastructure.Time;
using EnglishApp.Shared.Dtos;
using EnglishApp.Shared.Enums;

namespace EnglishApp.Api.Services.Ai;

/// <summary>Son 7 günün verisinden haftalık kişisel koç raporu (AI). Hafta başına (Pazartesi) bir kayıt tutulur.</summary>
public interface ICoachService
{
    Task<CoachReportDto?> GetLatestAsync(CancellationToken ct);
    Task<CoachReportDto> GenerateAsync(CancellationToken ct);
    /// <summary>Haftalık mail için: bu haftanın raporu yoksa üretir. Hata olursa null döner (mail yine gider).</summary>
    Task<CoachReportDto?> GetOrCreateForUserAsync(int userId, CancellationToken ct);
}

public sealed class CoachService(
    IDbConnectionFactory db,
    ILlmService llm,
    IStatsRepository stats,
    ICurrentUser currentUser,
    ITrClock clock,
    ILogger<CoachService> logger) : ICoachService
{
    private sealed class ReportRow
    {
        public DateTime WeekStart { get; set; }
        public string ContentJson { get; set; } = "";
        public DateTime CreatedAt { get; set; }
    }

    private sealed class CoachContent
    {
        public string SummaryTr { get; set; } = "";
        public List<string> WinsTr { get; set; } = [];
        public List<CoachFocusDto> Focus { get; set; } = [];
        public List<PhraseDto> Phrases { get; set; } = [];
        public List<string> NextWeekPlanTr { get; set; } = [];
    }

    public static DateTime WeekStartOf(DateTime day) => day.Date.AddDays(-(((int)day.DayOfWeek + 6) % 7));

    public async Task<CoachReportDto?> GetLatestAsync(CancellationToken ct)
    {
        await using var c = await db.OpenAsync(ct);
        var row = await c.QueryFirstOrDefaultAsync<ReportRow>(new CommandDefinition(
            "SELECT TOP (1) WeekStart, ContentJson, CreatedAt FROM ai.CoachReports WHERE UserId = @userId ORDER BY WeekStart DESC",
            new { userId = currentUser.Id }, cancellationToken: ct));
        return row is null ? null : ToDto(row);
    }

    public Task<CoachReportDto> GenerateAsync(CancellationToken ct) => GenerateForUserAsync(currentUser.Id, ct);

    public async Task<CoachReportDto?> GetOrCreateForUserAsync(int userId, CancellationToken ct)
    {
        try
        {
            var weekStart = WeekStartOf(clock.Today);
            await using (var c = await db.OpenAsync(ct))
            {
                var row = await c.QueryFirstOrDefaultAsync<ReportRow>(new CommandDefinition(
                    "SELECT WeekStart, ContentJson, CreatedAt FROM ai.CoachReports WHERE UserId = @userId AND WeekStart = @weekStart",
                    new { userId, weekStart }, cancellationToken: ct));
                if (row is not null) return ToDto(row);
            }
            return await GenerateForUserAsync(userId, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Haftalık koç raporu üretilemedi (mail raporsuz gidecek). Kullanıcı: {UserId}", userId);
            return null;
        }
    }

    private async Task<CoachReportDto> GenerateForUserAsync(int userId, CancellationToken ct)
    {
        var today = clock.Today;
        var weekStart = WeekStartOf(today);
        var data = await BuildDataAsync(userId, today, ct);

        var r = await llm.GenerateJsonAsync<CoachContent>(userId, AiFeature.Other,
            [LlmMessage.System(LearningPrompts.CoachSystem()), LlmMessage.User(data)],
            LearningPrompts.CoachSchema, 0.5, x => !string.IsNullOrWhiteSpace(x.SummaryTr) && x.Focus.Count > 0, ct);

        var content = r.Data;
        content.WinsTr = content.WinsTr.Where(s => !string.IsNullOrWhiteSpace(s)).Take(3).ToList();
        content.Focus = content.Focus.Where(f => !string.IsNullOrWhiteSpace(f.TitleTr)).Take(3).ToList();
        content.Phrases = content.Phrases.Where(p => !string.IsNullOrWhiteSpace(p.En)).Take(6).ToList();
        content.NextWeekPlanTr = content.NextWeekPlanTr.Where(s => !string.IsNullOrWhiteSpace(s)).Take(5).ToList();
        var json = JsonSerializer.Serialize(content, JsonDefaults.Options);
        var now = clock.Now;

        await using var c = await db.OpenAsync(ct);
        await c.ExecuteAsync(new CommandDefinition(
            """
            UPDATE ai.CoachReports SET ContentJson = @json, CreatedAt = @now WHERE UserId = @userId AND WeekStart = @weekStart;
            IF @@ROWCOUNT = 0
                INSERT INTO ai.CoachReports (UserId, WeekStart, ContentJson, CreatedAt) VALUES (@userId, @weekStart, @json, @now);
            """, new { userId, weekStart, json, now }, cancellationToken: ct));

        logger.LogInformation("Haftalık koç raporu üretildi. Kullanıcı: {UserId}, hafta: {WeekStart:yyyy-MM-dd}", userId, weekStart);
        return ToDto(new ReportRow { WeekStart = weekStart, ContentJson = json, CreatedAt = now });
    }

    /// <summary>Modele gidecek veri özeti (kısa tutulur: token tasarrufu).</summary>
    private async Task<string> BuildDataAsync(int userId, DateTime today, CancellationToken ct)
    {
        var from = today.AddDays(-6);
        var days = await stats.GetDailyStatsAsync(userId, from, today, ct);
        var struggling = await stats.GetStrugglingWordsAsync(userId, 10, ct);

        await using var c = await db.OpenAsync(ct);
        var chat = (await c.QueryAsync<(string Content, string FeedbackJson)>(new CommandDefinition(
            """
            SELECT TOP (40) m.Content, m.FeedbackJson
            FROM ai.Messages m JOIN ai.Conversations cv ON cv.Id = m.ConversationId
            WHERE cv.UserId = @userId AND m.Role = 2 AND m.FeedbackJson IS NOT NULL AND m.CreatedAt >= @from
            ORDER BY m.Id DESC
            """, new { userId, from }, cancellationToken: ct))).ToList();

        var writing = (await c.QueryAsync<(string UserText, string? CorrectedText, byte? Score)>(new CommandDefinition(
            """
            SELECT TOP (4) UserText, CorrectedText, Score FROM ai.WritingSubmissions
            WHERE UserId = @userId AND CreatedAt >= @from ORDER BY CreatedAt DESC
            """, new { userId, from }, cancellationToken: ct))).ToList();

        var dictation = (await c.QueryAsync<(string TextEn, byte ScorePercent, string? TypedText)>(new CommandDefinition(
            """
            SELECT TOP (8) s.TextEn, a.ScorePercent, a.TypedText
            FROM study.DictationAttempts a JOIN content.Sentences s ON s.Id = a.SentenceId
            WHERE a.UserId = @userId AND a.CreatedAt >= @from AND a.ScorePercent < 80
            ORDER BY a.ScorePercent, a.CreatedAt DESC
            """, new { userId, from }, cancellationToken: ct))).ToList();
        var dictationAll = await c.QuerySingleAsync<(int Count, int? Avg)>(new CommandDefinition(
            "SELECT COUNT(*), AVG(CAST(ScorePercent AS int)) FROM study.DictationAttempts WHERE UserId = @userId AND CreatedAt >= @from",
            new { userId, from }, cancellationToken: ct));

        var quiz = await c.QuerySingleAsync<(int Count, int? Correct, int? Total)>(new CommandDefinition(
            "SELECT COUNT(*), SUM(CAST(CorrectCount AS int)), SUM(CAST(TotalCount AS int)) FROM study.QuizAttempts WHERE UserId = @userId AND CreatedAt >= @from",
            new { userId, from }, cancellationToken: ct));

        var reviews = days.Sum(d => d.ReviewCount);
        var correct = days.Sum(d => d.CorrectCount);
        var sb = new StringBuilder();
        sb.AppendLine($"Period: {from:yyyy-MM-dd} .. {today:yyyy-MM-dd}");
        sb.AppendLine($"Active days: {days.Count(d => d.ReviewCount > 0 || d.AiMessages > 0)}/7, study minutes: {Math.Round(days.Sum(d => d.StudySeconds) / 60.0)}, " +
                      $"new words: {days.Sum(d => d.NewCount)}, card reviews: {reviews}, accuracy: {(reviews == 0 ? "-" : $"{100 * correct / reviews}%")}, AI requests (all AI features, not only chat): {days.Sum(d => d.AiMessages)}");
        sb.AppendLine($"Listening tests: {quiz.Count}" + (quiz.Total > 0 ? $", score {100 * (quiz.Correct ?? 0) / quiz.Total}%" : ""));
        sb.AppendLine($"Dictation attempts: {dictationAll.Count}" + (dictationAll.Avg is { } avg ? $", average {avg}%" : ""));

        if (struggling.Count > 0)
            sb.AppendLine("Words often forgotten: " + string.Join("; ", struggling.Select(w => $"{w.Lemma} ({w.MeaningTr}, forgot {w.Lapses}x)")));

        var mistakes = chat.Select(m => (m.Content, Fb: Deserialize<MessageFeedbackDto>(m.FeedbackJson)))
            .Where(m => m.Fb is { IsCorrect: false } && !string.IsNullOrWhiteSpace(m.Fb.Corrected)).Take(8).ToList();
        if (mistakes.Count > 0)
        {
            sb.AppendLine("Chat mistakes (learner wrote -> correct):");
            foreach (var m in mistakes) sb.AppendLine($"- \"{Cut(m.Content, 160)}\" -> \"{Cut(m.Fb!.Corrected, 160)}\"");
        }
        if (writing.Count > 0)
        {
            sb.AppendLine("Writing (learner text -> corrected, score):");
            foreach (var w in writing) sb.AppendLine($"- \"{Cut(w.UserText, 250)}\" -> \"{Cut(w.CorrectedText, 250)}\" ({w.Score?.ToString() ?? "-"})");
        }
        if (dictation.Count > 0)
        {
            sb.AppendLine("Weak dictation (heard sentence -> learner typed, score):");
            foreach (var d in dictation) sb.AppendLine($"- \"{d.TextEn}\" -> \"{Cut(d.TypedText, 160)}\" ({d.ScorePercent}%)");
        }
        return sb.ToString();
    }

    private static CoachReportDto ToDto(ReportRow row)
    {
        var x = Deserialize<CoachContent>(row.ContentJson) ?? new CoachContent();
        return new CoachReportDto
        {
            WeekStart = row.WeekStart, CreatedAt = row.CreatedAt, SummaryTr = x.SummaryTr, WinsTr = x.WinsTr,
            Focus = x.Focus, Phrases = x.Phrases, NextWeekPlanTr = x.NextWeekPlanTr
        };
    }

    private static T? Deserialize<T>(string? json) where T : class
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<T>(json, JsonDefaults.Options); }
        catch (JsonException) { return null; }
    }

    private static string Cut(string? s, int max) => s is null ? "" : s.Length <= max ? s : s[..max] + "…";
}
