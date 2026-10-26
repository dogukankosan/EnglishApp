using Dapper;
using EnglishApp.Api.Infrastructure.Data;
using EnglishApp.Api.Infrastructure.Errors;
using EnglishApp.Api.Infrastructure.Llm;
using EnglishApp.Api.Infrastructure.Security;
using EnglishApp.Api.Infrastructure.Time;
using EnglishApp.Shared.Dtos;
using EnglishApp.Shared.Enums;
using FluentValidation;
using Microsoft.Data.SqlClient;

namespace EnglishApp.Api.Services.Ai;

/// <summary>
/// Kullanıcıya özel içerik (yakalanan cümleler, seyahat kiti kalıpları) yazar ve kartlarını açar.
/// Kişisel cümleler OwnerUserId ile işaretlenir: başka kullanıcılara örnek cümle olarak görünmez.
/// </summary>
public sealed class PersonalContentWriter(ITrClock clock)
{
    public sealed record SaveResult(int SentenceId, int NewWordCards, bool SentenceCard);

    public async Task<SaveResult> SaveAsync(SqlConnection c, SqlTransaction tx, int userId, string textEn, string textTr,
        string? sourceNote, string level, IEnumerable<CaptureItemDto> items, CancellationToken ct)
    {
        var now = clock.Now;
        var lvl = (byte)(Enum.TryParse<CefrLevel>(level, true, out var l) ? l : CefrLevel.A2);

        var sentenceId = await c.ExecuteScalarAsync<int>(new CommandDefinition(
            """
            INSERT INTO content.Sentences (ScenarioId, LineOrder, SpeakerRole, TextEn, TextTr, Level, Status, CreatedAt, OwnerUserId, SourceNote)
            VALUES (NULL, NULL, NULL, @textEn, @textTr, @lvl, 1, @now, @userId, @sourceNote);
            SELECT CAST(SCOPE_IDENTITY() AS int);
            """, new { textEn = Cut(textEn, 500), textTr = Cut(textTr, 500), lvl, now, userId, sourceNote = Cut(sourceNote, 200) }, tx, cancellationToken: ct));

        var newWordCards = 0;
        foreach (var item in items.Where(i => !string.IsNullOrWhiteSpace(i.Lemma)).Take(8))
        {
            var lemma = Cut(item.Lemma.Trim(), 100)!;
            var pos = (byte)(Enum.TryParse<PartOfSpeech>(item.Pos, true, out var p) ? p : (lemma.Contains(' ') ? PartOfSpeech.Phrase : PartOfSpeech.Other));

            var wordId = await c.ExecuteScalarAsync<int>(new CommandDefinition(
                """
                DECLARE @id int = (SELECT Id FROM content.Words WHERE Lemma = @lemma AND PartOfSpeech = @pos);
                IF @id IS NULL
                BEGIN
                    INSERT INTO content.Words (Lemma, PartOfSpeech, Level, Ipa, MeaningTr, UsageNoteTr, Status, CreatedAt, OwnerUserId)
                    VALUES (@lemma, @pos, @lvl, @ipa, @meaning, @note, 1, @now, @userId);
                    SET @id = CAST(SCOPE_IDENTITY() AS int);
                END
                SELECT @id;
                """,
                new
                {
                    lemma, pos, lvl, ipa = Cut(Blank(item.Ipa), 100), meaning = Cut(item.MeaningTr.Trim(), 300) ?? "",
                    note = Cut(Blank(item.NoteTr), 500), now, userId
                }, tx, cancellationToken: ct));

            if (ContentSeeder.FindWord(textEn, lemma) is { } m)
                await c.ExecuteAsync(new CommandDefinition(
                    """
                    IF NOT EXISTS (SELECT 1 FROM content.SentenceWords WHERE SentenceId = @sentenceId AND WordId = @wordId)
                        INSERT INTO content.SentenceWords (SentenceId, WordId, CharStart, CharLength) VALUES (@sentenceId, @wordId, @start, @len)
                    """, new { sentenceId, wordId, start = (short)m.Start, len = (short)m.Length }, tx, cancellationToken: ct));

            newWordCards += await AddCardAsync(c, tx, userId, CardType.WordMeaning, wordId, null, now, ct);
        }

        var sentenceCard = await AddCardAsync(c, tx, userId, CardType.SentenceListening, null, sentenceId, now, ct) > 0;
        return new SaveResult(sentenceId, newWordCards, sentenceCard);
    }

    /// <summary>Kart yoksa açar (hemen çalışılabilir). Açıldıysa 1 döner.</summary>
    private static Task<int> AddCardAsync(SqlConnection c, SqlTransaction tx, int userId, CardType type, int? wordId, int? sentenceId, DateTime now, CancellationToken ct) =>
        c.ExecuteScalarAsync<int>(new CommandDefinition(
            """
            IF EXISTS (SELECT 1 FROM study.Cards WHERE UserId = @userId AND CardType = @type
                       AND ((@wordId IS NULL AND WordId IS NULL) OR WordId = @wordId)
                       AND ((@sentenceId IS NULL AND SentenceId IS NULL) OR SentenceId = @sentenceId))
                SELECT 0;
            ELSE
            BEGIN
                INSERT INTO study.Cards (UserId, CardType, WordId, SentenceId, DueAt, CreatedAt) VALUES (@userId, @type, @wordId, @sentenceId, @now, @now);
                SELECT 1;
            END
            """, new { userId, type = (byte)type, wordId, sentenceId, now }, tx, cancellationToken: ct));

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    private static string? Cut(string? s, int max) => s is null || s.Length <= max ? s : s[..max];
}

// ---------- Cümle yakalama ----------

public interface ICaptureService
{
    Task<CaptureAnalysisDto> AnalyzeAsync(CaptureAnalyzeRequest req, CancellationToken ct);
    Task<CaptureSaveResultDto> SaveAsync(CaptureSaveRequest req, CancellationToken ct);
    Task<IReadOnlyList<CapturedSentenceDto>> ListAsync(CancellationToken ct);
    Task DeleteAsync(int sentenceId, CancellationToken ct);
}

public sealed class CaptureService(
    IDbConnectionFactory db,
    ILlmService llm,
    PersonalContentWriter writer,
    ICurrentUser currentUser,
    ILogger<CaptureService> logger) : ICaptureService
{
    public async Task<CaptureAnalysisDto> AnalyzeAsync(CaptureAnalyzeRequest req, CancellationToken ct)
    {
        var text = req.Text.Trim();
        var r = await llm.GenerateJsonAsync<CaptureAnalysisDto>(currentUser.Id, AiFeature.WordExplain,
            [LlmMessage.System(PracticePrompts.CaptureSystem()), LlmMessage.User(text)],
            PracticePrompts.CaptureSchema, 0.3, a => !string.IsNullOrWhiteSpace(a.TranslationTr), ct);

        var a = r.Data;
        a.Text = text;
        a.CorrectedText = string.IsNullOrWhiteSpace(a.CorrectedText) || a.CorrectedText.Trim() == text ? null : a.CorrectedText.Trim();
        a.Items = a.Items.Where(i => !string.IsNullOrWhiteSpace(i.Lemma)).Take(6).ToList();
        return a;
    }

    public async Task<CaptureSaveResultDto> SaveAsync(CaptureSaveRequest req, CancellationToken ct)
    {
        var userId = currentUser.Id;
        var result = await db.InTransactionAsync((c, tx) =>
            writer.SaveAsync(c, tx, userId, req.Text.Trim(), req.TranslationTr.Trim(), req.SourceNote, req.Level, req.Items, ct), ct);

        logger.LogInformation("Cümle yakalandı: {SentenceId}, {Cards} yeni kelime kartı", result.SentenceId, result.NewWordCards);
        return new CaptureSaveResultDto { SentenceId = result.SentenceId, NewWordCards = result.NewWordCards, SentenceCard = result.SentenceCard };
    }

    public async Task<IReadOnlyList<CapturedSentenceDto>> ListAsync(CancellationToken ct)
    {
        await using var c = await db.OpenAsync(ct);
        var rows = await c.QueryAsync<(int SentenceId, string TextEn, string TextTr, string? SourceNote, DateTime CreatedAt, string? Words)>(new CommandDefinition(
            """
            SELECT TOP (50) s.Id AS SentenceId, s.TextEn, s.TextTr, s.SourceNote, s.CreatedAt,
                   (SELECT STRING_AGG(w.Lemma, '|') FROM content.SentenceWords sw JOIN content.Words w ON w.Id = sw.WordId WHERE sw.SentenceId = s.Id) AS Words
            FROM content.Sentences s
            WHERE s.OwnerUserId = @userId AND (s.SourceNote IS NULL OR s.SourceNote NOT LIKE 'Seyahat kiti:%')
            ORDER BY s.Id DESC
            """, new { userId = currentUser.Id }, cancellationToken: ct));

        return rows.Select(r => new CapturedSentenceDto
        {
            SentenceId = r.SentenceId, TextEn = r.TextEn, TextTr = r.TextTr, SourceNote = r.SourceNote, CreatedAt = r.CreatedAt,
            Words = string.IsNullOrEmpty(r.Words) ? [] : r.Words.Split('|').ToList()
        }).ToList();
    }

    /// <summary>Yakalanan cümleyi ve kartını siler (kelime kartları kalır).</summary>
    public async Task DeleteAsync(int sentenceId, CancellationToken ct)
    {
        var userId = currentUser.Id;
        var deleted = await db.InTransactionAsync(async (c, tx) =>
        {
            var owned = await c.ExecuteScalarAsync<bool>(new CommandDefinition(
                "SELECT CASE WHEN EXISTS (SELECT 1 FROM content.Sentences WHERE Id = @sentenceId AND OwnerUserId = @userId) THEN 1 ELSE 0 END",
                new { sentenceId, userId }, tx, cancellationToken: ct));
            if (!owned) return false;

            await c.ExecuteAsync(new CommandDefinition(
                """
                DELETE r FROM study.ReviewLogs r JOIN study.Cards cd ON cd.Id = r.CardId WHERE cd.SentenceId = @sentenceId;
                DELETE FROM study.Cards WHERE SentenceId = @sentenceId;
                DELETE FROM content.SentenceWords WHERE SentenceId = @sentenceId;
                DELETE FROM content.Sentences WHERE Id = @sentenceId AND OwnerUserId = @userId;
                """, new { sentenceId, userId }, tx, cancellationToken: ct));
            return true;
        }, ct);

        if (!deleted) throw AppException.NotFound("Cümle bulunamadı.");
    }
}

public sealed class CaptureAnalyzeRequestValidator : AbstractValidator<CaptureAnalyzeRequest>
{
    public CaptureAnalyzeRequestValidator() =>
        RuleFor(x => x.Text).NotEmpty().WithMessage("Bir cümle yapıştır.")
            .MinimumLength(2).MaximumLength(400).WithMessage("Cümle en fazla 400 karakter olabilir.");
}

public sealed class CaptureSaveRequestValidator : AbstractValidator<CaptureSaveRequest>
{
    public CaptureSaveRequestValidator()
    {
        RuleFor(x => x.Text).NotEmpty().MaximumLength(400);
        RuleFor(x => x.TranslationTr).NotEmpty().WithMessage("Türkçe çeviri boş olamaz.").MaximumLength(500);
        RuleFor(x => x.SourceNote).MaximumLength(200);
        RuleFor(x => x.Items).Must(i => i.Count <= 8).WithMessage("En fazla 8 kelime/kalıp seçebilirsin.");
        RuleForEach(x => x.Items).ChildRules(i =>
        {
            i.RuleFor(w => w.Lemma).NotEmpty().MaximumLength(100);
            i.RuleFor(w => w.MeaningTr).NotEmpty().WithMessage("Kelimenin Türkçe anlamı boş olamaz.").MaximumLength(300);
        });
    }
}
