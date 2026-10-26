using System.Text.Json;
using Dapper;
using EnglishApp.Api.Infrastructure;
using EnglishApp.Api.Infrastructure.Data;
using EnglishApp.Api.Infrastructure.Errors;
using EnglishApp.Api.Infrastructure.Llm;
using EnglishApp.Api.Infrastructure.Security;
using EnglishApp.Api.Infrastructure.Time;
using EnglishApp.Shared.Dtos;
using EnglishApp.Shared.Enums;
using FluentValidation;

namespace EnglishApp.Api.Services.Ai;

public interface IWritingService
{
    IReadOnlyList<WritingTaskDto> GetTasks();
    Task<WritingResultDto> SubmitAsync(WritingSubmitRequest req, CancellationToken ct);
    Task<WritingResultDto> GetAsync(long id, CancellationToken ct);
    Task<IReadOnlyList<WritingListItemDto>> ListAsync(CancellationToken ct);
    Task DeleteAsync(long id, CancellationToken ct);
}

public sealed class WritingService(
    IDbConnectionFactory db,
    ILlmService llm,
    ICurrentUser currentUser,
    ITrClock clock,
    ILogger<WritingService> logger) : IWritingService
{
    /// <summary>Gerçek hayattan, kısa yazma görevleri (seviyeye göre sıralı).</summary>
    public static readonly IReadOnlyList<WritingTaskDto> Tasks =
    [
        new() { Code = "intro", Level = CefrLevel.A1, MinWords = 30, CategoryId = 8,
            TitleTr = "Kendini tanıt", InstructionTr = "Yeni tanıştığın birine kendini tanıt: adın, yaşın, nerede yaşadığın, ne iş yaptığın ve hobilerin.",
            HintEn = "Hi! My name is ... I'm ... years old. I live in ... I work as a ..." },
        new() { Code = "hotel-booking", Level = CefrLevel.A2, MinWords = 40, CategoryId = 2,
            TitleTr = "Otele rezervasyon maili", InstructionTr = "Bir otele e-posta yaz: 3 gecelik tek kişilik oda istiyorsun. Tarihleri söyle, kahvaltının dahil olup olmadığını ve fiyatı sor.",
            HintEn = "Dear Sir or Madam, I would like to book ..." },
        new() { Code = "hotel-complaint", Level = CefrLevel.A2, MinWords = 40, CategoryId = 2,
            TitleTr = "Resepsiyona şikayet", InstructionTr = "Otel odanda klima çalışmıyor ve havlu yok. Resepsiyona kibar bir mesaj yaz ve çözüm iste.",
            HintEn = "Hello, I'm in room ... Unfortunately, ..." },
        new() { Code = "airport-lost-bag", Level = CefrLevel.A2, MinWords = 40, CategoryId = 1,
            TitleTr = "Kayıp bavul", InstructionTr = "Havalimanında bavulun gelmedi. Havayoluna kısa bir mesaj yaz: uçuş numaran, bavulunun rengi ve içinde ne olduğu, nerede kaldığın.",
            HintEn = "My suitcase didn't arrive. My flight number was ..." },
        new() { Code = "restaurant-review", Level = CefrLevel.A2, MinWords = 40, CategoryId = 3,
            TitleTr = "Restoran yorumu", InstructionTr = "Tatilde gittiğin bir restoran için kısa bir yorum yaz: ne yedin, nasıldı, garson nasıldı, tavsiye eder misin?",
            HintEn = "Last night we had dinner at ... The food was ..." },
        new() { Code = "pharmacy-note", Level = CefrLevel.A2, MinWords = 30, CategoryId = 6,
            TitleTr = "Eczacıya şikayetini anlat", InstructionTr = "Eczacıya ne hissettiğini yaz: belirtilerin neler, ne zamandan beri, hangi ilaca ihtiyacın olabilir.",
            HintEn = "I don't feel well. I have ... since ..." },
        new() { Code = "directions", Level = CefrLevel.A2, MinWords = 40, CategoryId = 7,
            TitleTr = "Yol tarifi ver", InstructionTr = "Otelinden en yakın metro istasyonuna nasıl gidileceğini bir turiste anlat.",
            HintEn = "Go out of the hotel and turn left ..." },
        new() { Code = "weekend", Level = CefrLevel.A2, MinWords = 50, CategoryId = 8,
            TitleTr = "Hafta sonun nasıldı?", InstructionTr = "Yurt dışındaki bir arkadaşına geçen hafta sonu ne yaptığını anlatan bir mesaj yaz. Geçmiş zaman kullan.",
            HintEn = "Last weekend I ..." },
        new() { Code = "trip-plan", Level = CefrLevel.B1, MinWords = 60, CategoryId = 1,
            TitleTr = "Seyahat planın", InstructionTr = "Gelecek yaz gitmek istediğin ülkeyi, neden gitmek istediğini ve orada ne yapacağını anlat.",
            HintEn = "Next summer I'm going to ... because ..." },
        new() { Code = "work-intro", Level = CefrLevel.B1, MinWords = 60, CategoryId = 11,
            TitleTr = "İşini anlat", InstructionTr = "Yazılımcı olarak işini yabancı bir meslektaşına anlat: ne üzerinde çalışıyorsun, bir günün nasıl geçiyor, neyi seviyorsun.",
            HintEn = "I'm a software developer. At the moment I'm working on ..." }
    ];

    public IReadOnlyList<WritingTaskDto> GetTasks() => Tasks;

    public async Task<WritingResultDto> SubmitAsync(WritingSubmitRequest req, CancellationToken ct)
    {
        var userId = currentUser.Id;
        var task = req.TaskCode is null ? null : Tasks.FirstOrDefault(t => t.Code == req.TaskCode)
                   ?? throw new AppException(ErrorCodes.ValidationError, "Yazma görevi bulunamadı.");
        var promptText = task is not null ? task.InstructionTr : req.PromptText?.Trim();
        if (string.IsNullOrWhiteSpace(promptText)) promptText = "Serbest yazı (konu kullanıcıya ait)";

        var text = req.Text.Trim();
        var result = await llm.GenerateJsonAsync<AiPrompts.WritingFeedback>(userId, AiFeature.Writing,
            [LlmMessage.System(AiPrompts.WritingSystem(promptText)), LlmMessage.User(text)],
            AiPrompts.WritingSchema, 0.3, f => !string.IsNullOrWhiteSpace(f.CorrectedText), ct);

        var f = result.Data;
        f.Score = Math.Clamp(f.Score, 0, 100);
        f.Level = NormalizeLevel(f.Level);
        f.Mistakes = f.Mistakes.Take(10).ToList();
        f.NewPhrases = f.NewPhrases.Take(6).ToList();

        await using var c = await db.OpenAsync(ct);
        var id = await c.ExecuteScalarAsync<long>(new CommandDefinition(
            """
            INSERT INTO ai.WritingSubmissions (UserId, ScenarioId, PromptText, UserText, CorrectedText, FeedbackJson, Score, TaskCode, EstimatedLevel, CreatedAt)
            VALUES (@userId, NULL, @promptText, @text, @corrected, @feedback, @score, @taskCode, @level, @now);
            SELECT CAST(SCOPE_IDENTITY() AS bigint);
            """,
            new
            {
                userId, promptText, text, corrected = f.CorrectedText, feedback = JsonSerializer.Serialize(f, JsonDefaults.Options),
                score = (byte)f.Score, taskCode = task?.Code, level = f.Level, now = clock.Now
            }, cancellationToken: ct));

        logger.LogInformation("Yazma çalışması değerlendirildi. Id: {WritingId}, Puan: {Score}, Seviye: {Level}", id, f.Score, f.Level);
        var dto = ToDto(id, task?.Code, promptText, text, f, clock.Now);
        dto.Quota = await llm.GetQuotaAsync(userId, ct);
        return dto;
    }

    public async Task<WritingResultDto> GetAsync(long id, CancellationToken ct)
    {
        await using var c = await db.OpenAsync(ct);
        var row = await c.QuerySingleOrDefaultAsync<(long Id, string? TaskCode, string? PromptText, string UserText, string? FeedbackJson, DateTime CreatedAt)>(
            new CommandDefinition(
                "SELECT Id, TaskCode, PromptText, UserText, FeedbackJson, CreatedAt FROM ai.WritingSubmissions WHERE Id = @id AND UserId = @userId",
                new { id, userId = currentUser.Id }, cancellationToken: ct));
        if (row.Id == 0) throw AppException.NotFound("Yazı bulunamadı.");

        var f = string.IsNullOrEmpty(row.FeedbackJson) ? new AiPrompts.WritingFeedback()
            : JsonSerializer.Deserialize<AiPrompts.WritingFeedback>(row.FeedbackJson, JsonDefaults.Options) ?? new();
        return ToDto(row.Id, row.TaskCode, row.PromptText, row.UserText, f, row.CreatedAt);
    }

    public async Task<IReadOnlyList<WritingListItemDto>> ListAsync(CancellationToken ct)
    {
        await using var c = await db.OpenAsync(ct);
        return (await c.QueryAsync<WritingListItemDto>(new CommandDefinition(
            """
            SELECT TOP (50) Id, PromptText, CAST(Score AS int) AS Score, EstimatedLevel,
                   LEFT(UserText, 120) AS Preview, CreatedAt
            FROM ai.WritingSubmissions WHERE UserId = @userId ORDER BY CreatedAt DESC
            """, new { userId = currentUser.Id }, cancellationToken: ct))).ToList();
    }

    public async Task DeleteAsync(long id, CancellationToken ct)
    {
        await using var c = await db.OpenAsync(ct);
        var n = await c.ExecuteAsync(new CommandDefinition(
            "DELETE FROM ai.WritingSubmissions WHERE Id = @id AND UserId = @userId", new { id, userId = currentUser.Id }, cancellationToken: ct));
        if (n == 0) throw AppException.NotFound("Yazı bulunamadı.");
    }

    private static WritingResultDto ToDto(long id, string? taskCode, string? prompt, string text, AiPrompts.WritingFeedback f, DateTime createdAt) => new()
    {
        Id = id,
        TaskCode = taskCode,
        PromptText = prompt,
        UserText = text,
        CorrectedText = f.CorrectedText,
        Score = f.Score,
        Level = f.Level,
        SummaryTr = f.SummaryTr,
        Mistakes = f.Mistakes,
        BetterVersion = f.BetterVersion,
        NewPhrases = f.NewPhrases,
        CreatedAt = createdAt
    };

    private static string NormalizeLevel(string? level)
    {
        var l = (level ?? "").Trim().ToUpperInvariant();
        return l is "A1" or "A2" or "B1" or "B2" or "C1" or "C2" ? l : "A2";
    }
}

public sealed class WritingSubmitRequestValidator : AbstractValidator<WritingSubmitRequest>
{
    public WritingSubmitRequestValidator()
    {
        RuleFor(x => x.Text).NotEmpty().WithMessage("Yazı boş olamaz.")
            .MinimumLength(15).WithMessage("Biraz daha yaz: en az 15 karakter.")
            .MaximumLength(2000).WithMessage("Yazı en fazla 2000 karakter olabilir.");
        RuleFor(x => x.PromptText).MaximumLength(300);
        RuleFor(x => x.TaskCode).MaximumLength(50);
    }
}
