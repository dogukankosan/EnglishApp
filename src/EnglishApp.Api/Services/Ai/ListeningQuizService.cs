using System.Text.Json;
using Dapper;
using EnglishApp.Api.Data;
using EnglishApp.Api.Infrastructure;
using EnglishApp.Api.Infrastructure.Data;
using EnglishApp.Api.Infrastructure.Errors;
using EnglishApp.Api.Infrastructure.Files;
using EnglishApp.Api.Infrastructure.Llm;
using EnglishApp.Api.Infrastructure.Security;
using EnglishApp.Api.Infrastructure.Time;
using EnglishApp.Shared.Dtos;
using EnglishApp.Shared.Enums;
using FluentValidation;

namespace EnglishApp.Api.Services.Ai;

/// <summary>
/// Dinleme testi: senaryo diyaloğunu metinsiz dinle, Türkçe soruları cevapla.
/// Sorular senaryo başına bir kez AI ile üretilir ve herkes için saklanır; doğru cevaplar istemciye gönderilmez.
/// </summary>
public interface IListeningQuizService
{
    Task<IReadOnlyList<ListeningScenarioDto>> ListAsync(CancellationToken ct);
    Task<ListeningQuizDto> GetAsync(int scenarioId, CancellationToken ct);
    Task<QuizResultDto> SubmitAsync(QuizSubmitRequest req, CancellationToken ct);
    Task<ListeningSummaryDto> GetSummaryAsync(int userId, CancellationToken ct);
}

public sealed class ListeningQuizService(
    IDbConnectionFactory db,
    IContentRepository content,
    IAudioStore audio,
    ILlmService llm,
    ICurrentUser currentUser,
    ITrClock clock,
    ILogger<ListeningQuizService> logger) : IListeningQuizService
{
    public async Task<IReadOnlyList<ListeningScenarioDto>> ListAsync(CancellationToken ct)
    {
        await using var c = await db.OpenAsync(ct);
        return (await c.QueryAsync<ListeningScenarioDto>(new CommandDefinition(
            """
            SELECT sc.Id AS ScenarioId, sc.TitleTr, sc.TitleEn, sc.Level, cat.IconName,
                   COUNT(a.Id) AS Attempts,
                   MAX(100 * a.CorrectCount / a.TotalCount) AS BestPercent,
                   (SELECT TOP 1 100 * x.CorrectCount / x.TotalCount FROM study.QuizAttempts x
                    WHERE x.UserId = @userId AND x.ScenarioId = sc.Id ORDER BY x.CreatedAt DESC) AS LastPercent,
                   MAX(a.CreatedAt) AS LastAt
            FROM content.Scenarios sc
            JOIN content.ScenarioCategories cat ON cat.Id = sc.CategoryId
            LEFT JOIN study.QuizAttempts a ON a.ScenarioId = sc.Id AND a.UserId = @userId
            WHERE sc.Status = 1 AND EXISTS (SELECT 1 FROM content.Sentences s WHERE s.ScenarioId = sc.Id AND s.Status = 1)
            GROUP BY sc.Id, sc.TitleTr, sc.TitleEn, sc.Level, cat.IconName, cat.SortOrder, sc.SortOrder
            ORDER BY cat.SortOrder, sc.SortOrder
            """, new { userId = currentUser.Id }, cancellationToken: ct))).ToList();
    }

    public async Task<ListeningQuizDto> GetAsync(int scenarioId, CancellationToken ct)
    {
        var sc = (await content.ListScenariosAsync(currentUser.Id, scenarioId, ct)).FirstOrDefault()
                 ?? throw AppException.NotFound("Senaryo bulunamadı.");
        var lines = await content.GetDialogueAsync(scenarioId, ct);
        if (lines.Count == 0) throw new AppException("NO_DIALOGUE", "Bu senaryoda dinlenecek diyalog yok.");
        audio.FillDialogue(lines);

        var quiz = await LoadOrCreateQuizAsync(scenarioId, sc.TitleEn, lines, ct);
        return new ListeningQuizDto
        {
            ScenarioId = scenarioId,
            TitleTr = sc.TitleTr,
            TitleEn = sc.TitleEn,
            Level = sc.Level,
            Lines = lines,
            Questions = quiz.Questions.Select((q, i) => new QuizQuestionDto { Index = i, QuestionTr = q.QuestionTr, Options = q.Options }).ToList()
        };
    }

    public async Task<QuizResultDto> SubmitAsync(QuizSubmitRequest req, CancellationToken ct)
    {
        var userId = currentUser.Id;
        var quiz = await LoadQuizAsync(req.ScenarioId, ct) ?? throw AppException.NotFound("Bu senaryonun testi bulunamadı. Testi yeniden aç.");

        var items = quiz.Questions.Select((q, i) =>
        {
            int? your = i < req.Answers.Count && req.Answers[i] >= 0 ? req.Answers[i] : null;
            return new QuizResultItemDto
            {
                QuestionTr = q.QuestionTr, Options = q.Options, CorrectIndex = q.CorrectIndex,
                YourIndex = your, IsCorrect = your == q.CorrectIndex, ExplanationTr = q.ExplanationTr
            };
        }).ToList();
        var correct = items.Count(i => i.IsCorrect);

        await using (var c = await db.OpenAsync(ct))
        {
            await c.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO study.QuizAttempts (UserId, ScenarioId, CorrectCount, TotalCount, ReplayCount, Accent, CreatedAt)
                VALUES (@userId, @scenarioId, @correct, @total, @replays, @accent, @now)
                """,
                new
                {
                    userId, scenarioId = req.ScenarioId, correct = (byte)correct, total = (byte)items.Count,
                    replays = (byte)Math.Clamp(req.ReplayCount, 0, 255), accent = req.Accent is "uk" or "us" ? req.Accent : null, now = clock.Now
                }, cancellationToken: ct));
        }

        logger.LogInformation("Dinleme testi: senaryo {ScenarioId}, {Correct}/{Total}", req.ScenarioId, correct, items.Count);
        return new QuizResultDto
        {
            Correct = correct,
            Total = items.Count,
            ScorePercent = (int)Math.Round(100.0 * correct / items.Count),
            Items = items,
            Summary = await GetSummaryAsync(userId, ct)
        };
    }

    public async Task<ListeningSummaryDto> GetSummaryAsync(int userId, CancellationToken ct)
    {
        await using var c = await db.OpenAsync(ct);
        var row = await c.QuerySingleAsync<(int Attempts, int? Avg, DateTime? LastAt)>(new CommandDefinition(
            """
            SELECT COUNT(*) AS Attempts,
                   CAST(ROUND(AVG(100.0 * CorrectCount / TotalCount), 0) AS int) AS Avg,
                   (SELECT MAX(CreatedAt) FROM study.QuizAttempts WHERE UserId = @userId) AS LastAt
            FROM study.QuizAttempts
            WHERE UserId = @userId AND CreatedAt >= @from
            """, new { userId, from = clock.Today.AddDays(-30) }, cancellationToken: ct));

        return new ListeningSummaryDto
        {
            Attempts30d = row.Attempts,
            AveragePercent30d = row.Avg,
            LastAt = row.LastAt,
            // Ayda bir seviye ölçümü: son 30 günde hiç test yoksa hatırlat
            TestDue = row.LastAt is null || row.LastAt < clock.Now.AddDays(-30)
        };
    }

    // ---------- Soru üretimi ----------

    private async Task<PracticePrompts.QuizContent> LoadOrCreateQuizAsync(int scenarioId, string titleEn, List<DialogueLineDto> lines, CancellationToken ct)
    {
        var existing = await LoadQuizAsync(scenarioId, ct);
        if (existing is not null) return existing;

        var result = await llm.GenerateJsonAsync<PracticePrompts.QuizContent>(currentUser.Id, AiFeature.ContentGeneration,
            [LlmMessage.System(PracticePrompts.QuizSystem()), LlmMessage.User(PracticePrompts.QuizUser(titleEn, lines))],
            PracticePrompts.QuizSchema, 0.4, IsValid, ct);

        var quiz = result.Data;
        quiz.Questions = quiz.Questions.Take(6).ToList();

        await using var c = await db.OpenAsync(ct);
        // Aynı anda iki kişi açarsa ikinci kayıt atlanır
        await c.ExecuteAsync(new CommandDefinition(
            """
            IF NOT EXISTS (SELECT 1 FROM content.ListeningQuizzes WHERE ScenarioId = @scenarioId)
                INSERT INTO content.ListeningQuizzes (ScenarioId, QuestionsJson, Model, CreatedAt)
                VALUES (@scenarioId, @json, @model, @now)
            """,
            new { scenarioId, json = JsonSerializer.Serialize(quiz, JsonDefaults.Options), model = result.Model, now = clock.Now },
            cancellationToken: ct));

        logger.LogInformation("Dinleme testi üretildi: senaryo {ScenarioId}, {Count} soru ({Model})", scenarioId, quiz.Questions.Count, result.Model);
        return await LoadQuizAsync(scenarioId, ct) ?? quiz;
    }

    private async Task<PracticePrompts.QuizContent?> LoadQuizAsync(int scenarioId, CancellationToken ct)
    {
        await using var c = await db.OpenAsync(ct);
        var json = await c.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT QuestionsJson FROM content.ListeningQuizzes WHERE ScenarioId = @scenarioId", new { scenarioId }, cancellationToken: ct));
        return json is null ? null : JsonSerializer.Deserialize<PracticePrompts.QuizContent>(json, JsonDefaults.Options);
    }

    public static bool IsValid(PracticePrompts.QuizContent q) =>
        q.Questions.Count >= 3 &&
        q.Questions.All(x => !string.IsNullOrWhiteSpace(x.QuestionTr)
                             && x.Options.Count is >= 3 and <= 5
                             && x.Options.All(o => !string.IsNullOrWhiteSpace(o))
                             && x.CorrectIndex >= 0 && x.CorrectIndex < x.Options.Count);
}

public sealed class QuizSubmitRequestValidator : AbstractValidator<QuizSubmitRequest>
{
    public QuizSubmitRequestValidator()
    {
        RuleFor(x => x.ScenarioId).GreaterThan(0);
        RuleFor(x => x.Answers).NotNull().Must(a => a.Count <= 10).WithMessage("Geçersiz cevap listesi.");
        RuleFor(x => x.ReplayCount).InclusiveBetween(0, 100);
    }
}
