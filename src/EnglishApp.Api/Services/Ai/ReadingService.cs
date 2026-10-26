using System.Text.Json;
using System.Text.RegularExpressions;
using Dapper;
using EnglishApp.Api.Data;
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

/// <summary>
/// Kullanıcının bildiği kelimelerle (+ en fazla 8 yeni) AI okuma metni; dokun-çevir sözlük ve kartlara ekleme.
/// </summary>
public interface IReadingService
{
    Task<ReadingDto> CreateAsync(ReadingRequest req, CancellationToken ct);
    Task<IReadOnlyList<ReadingListItemDto>> ListAsync(CancellationToken ct);
    Task<ReadingDto> GetAsync(long id, CancellationToken ct);
    Task MarkReadAsync(long id, CancellationToken ct);
    Task DeleteAsync(long id, CancellationToken ct);
    Task<WordLookupDto> LookupAsync(WordLookupRequest req, CancellationToken ct);
    Task<CaptureSaveResultDto> AddWordAsync(AddWordToCardsRequest req, CancellationToken ct);
}

public sealed partial class ReadingService(
    IDbConnectionFactory db,
    ILlmService llm,
    IStatsRepository stats,
    PersonalContentWriter writer,
    ICurrentUser currentUser,
    ITrClock clock,
    ILogger<ReadingService> logger) : IReadingService
{
    private sealed class Row
    {
        public long Id { get; set; }
        public string Title { get; set; } = "";
        public byte Level { get; set; }
        public string? Topic { get; set; }
        public short WordCount { get; set; }
        public string ContentJson { get; set; } = "";
        public DateTime CreatedAt { get; set; }
        public DateTime? ReadAt { get; set; }
    }

    public async Task<ReadingDto> CreateAsync(ReadingRequest req, CancellationToken ct)
    {
        var userId = currentUser.Id;
        var topic = string.IsNullOrWhiteSpace(req.Topic) ? null : req.Topic.Trim();

        await using var c = await db.OpenAsync(ct);
        // Bildiği / öğrendiği kelimeler: en sağlamlar önce (token tasarrufu için en fazla 250)
        var known = (await c.QueryAsync<string>(new CommandDefinition(
            """
            SELECT TOP (250) Lemma FROM (
                SELECT w.Lemma, MAX(cd.Stability) AS S
                FROM study.Cards cd JOIN content.Words w ON w.Id = cd.WordId
                WHERE cd.UserId = @userId AND cd.CardType = 1 AND cd.State > 0
                GROUP BY w.Lemma
                UNION ALL
                SELECT w.Lemma, 1000 FROM study.UserKnownWords k JOIN content.Words w ON w.Id = k.WordId WHERE k.UserId = @userId
            ) x GROUP BY Lemma ORDER BY MAX(S) DESC
            """, new { userId }, cancellationToken: ct))).ToList();

        var level = req.Level ?? LevelFromVocabulary(known.Count);
        var r = await llm.GenerateJsonAsync<ReadingContentDto>(userId, AiFeature.ContentGeneration,
            [LlmMessage.System(LearningPrompts.ReadingSystem(level.ToString())), LlmMessage.User(LearningPrompts.ReadingUser(topic, known))],
            LearningPrompts.ReadingSchema, 0.7, x => x.Paragraphs.Count >= 2 && x.Paragraphs.All(p => !string.IsNullOrWhiteSpace(p.En)), ct);

        var content = Normalize(r.Data);
        var title = string.IsNullOrWhiteSpace(content.Title) ? "Reading" : content.Title.Trim();
        var wordCount = content.Paragraphs.Sum(p => CountWords(p.En));

        var id = await c.ExecuteScalarAsync<long>(new CommandDefinition(
            """
            INSERT INTO content.ReadingTexts (UserId, Title, Level, Topic, ContentJson, WordCount, CreatedAt)
            VALUES (@userId, @title, @level, @topic, @json, @wordCount, @now);
            SELECT CAST(SCOPE_IDENTITY() AS bigint);
            """,
            new
            {
                userId, title = Cut(title, 200), level = (byte)level, topic = Cut(topic, 150),
                json = JsonSerializer.Serialize(content, JsonDefaults.Options), wordCount = (short)Math.Min(wordCount, short.MaxValue), now = clock.Now
            }, cancellationToken: ct));

        logger.LogInformation("Okuma metni üretildi: {ReadingId} {Level}, {Words} kelime, {New} yeni kelime", id, level, wordCount, content.Glossary.Count);
        return await GetAsync(id, ct);
    }

    public async Task<IReadOnlyList<ReadingListItemDto>> ListAsync(CancellationToken ct)
    {
        await using var c = await db.OpenAsync(ct);
        return (await c.QueryAsync<ReadingListItemDto>(new CommandDefinition(
            """
            SELECT TOP (50) Id, Title, Level, Topic, WordCount, CreatedAt, ReadAt
            FROM content.ReadingTexts WHERE UserId = @userId ORDER BY CreatedAt DESC
            """, new { userId = currentUser.Id }, cancellationToken: ct))).ToList();
    }

    public async Task<ReadingDto> GetAsync(long id, CancellationToken ct)
    {
        var row = await LoadAsync(id, ct);
        return new ReadingDto
        {
            Id = row.Id, Title = row.Title, Level = (CefrLevel)row.Level, Topic = row.Topic, WordCount = row.WordCount,
            CreatedAt = row.CreatedAt, ReadAt = row.ReadAt,
            Content = JsonSerializer.Deserialize<ReadingContentDto>(row.ContentJson, JsonDefaults.Options) ?? new()
        };
    }

    public async Task MarkReadAsync(long id, CancellationToken ct)
    {
        await using var c = await db.OpenAsync(ct);
        await c.ExecuteAsync(new CommandDefinition(
            "UPDATE content.ReadingTexts SET ReadAt = @now WHERE Id = @id AND UserId = @userId AND ReadAt IS NULL",
            new { id, userId = currentUser.Id, now = clock.Now }, cancellationToken: ct));
    }

    public async Task DeleteAsync(long id, CancellationToken ct)
    {
        _ = await LoadAsync(id, ct);
        await using var c = await db.OpenAsync(ct);
        await c.ExecuteAsync(new CommandDefinition(
            "DELETE FROM content.ReadingTexts WHERE Id = @id AND UserId = @userId", new { id, userId = currentUser.Id }, cancellationToken: ct));
    }

    /// <summary>Önce uygulamanın sözlüğü (ücretsiz, anında); bulunamazsa hızlı AI modeli.</summary>
    public async Task<WordLookupDto> LookupAsync(WordLookupRequest req, CancellationToken ct)
    {
        var userId = currentUser.Id;
        var word = req.Word.Trim().Trim('.', ',', '!', '?', ';', ':', '"', '\'', '(', ')').ToLowerInvariant();
        if (word.Length == 0) throw new AppException(ErrorCodes.ValidationError, "Kelime boş olamaz.");

        await using var c = await db.OpenAsync(ct);
        var hit = await c.QueryFirstOrDefaultAsync<(int Id, string Lemma, byte Pos, string MeaningTr, string? Ipa)>(new CommandDefinition(
            """
            SELECT TOP (1) w.Id, w.Lemma, w.PartOfSpeech, w.MeaningTr, w.Ipa
            FROM content.Words w
            WHERE w.Lemma IN @forms AND w.MeaningTr <> N'' AND w.Status = 1 AND (w.OwnerUserId IS NULL OR w.OwnerUserId = @userId)
            ORDER BY CASE WHEN w.Lemma = @word THEN 0 ELSE 1 END, CASE WHEN w.FrequencyRank IS NULL THEN 1 ELSE 0 END, w.FrequencyRank
            """, new { forms = CandidateLemmas(word), word, userId }, cancellationToken: ct));

        if (hit.Id > 0)
        {
            var inCards = await c.ExecuteScalarAsync<bool>(new CommandDefinition(
                "SELECT CASE WHEN EXISTS (SELECT 1 FROM study.Cards WHERE UserId = @userId AND WordId = @id AND CardType = 1) THEN 1 ELSE 0 END",
                new { userId, id = hit.Id }, cancellationToken: ct));
            return new WordLookupDto
            {
                Lemma = hit.Lemma, Pos = ((PartOfSpeech)hit.Pos).ToString(), MeaningTr = hit.MeaningTr, Ipa = hit.Ipa,
                FromDictionary = true, AlreadyInCards = inCards
            };
        }

        var r = await llm.GenerateJsonAsync<WordLookupDto>(userId, AiFeature.WordExplain,
            [LlmMessage.System(LearningPrompts.LookupSystem()), LlmMessage.User(LearningPrompts.LookupUser(req))],
            LearningPrompts.LookupSchema, 0.2, x => !string.IsNullOrWhiteSpace(x.MeaningTr), ct, preferFast: true);
        var dto = r.Data;
        dto.Lemma = string.IsNullOrWhiteSpace(dto.Lemma) ? word : dto.Lemma.Trim();
        dto.Pos = Enum.TryParse<PartOfSpeech>(dto.Pos, true, out var p) ? p.ToString() : "Other";
        dto.Ipa = string.IsNullOrWhiteSpace(dto.Ipa) ? null : dto.Ipa.Trim();
        dto.FromDictionary = false;
        return dto;
    }

    public async Task<CaptureSaveResultDto> AddWordAsync(AddWordToCardsRequest req, CancellationToken ct)
    {
        var userId = currentUser.Id;
        var item = new CaptureItemDto { Lemma = req.Lemma.Trim(), Pos = req.Pos, MeaningTr = req.MeaningTr.Trim(), Ipa = req.Ipa };
        var result = await db.InTransactionAsync((c, tx) => writer.SaveAsync(c, tx, userId, req.Sentence.Trim(), req.SentenceTr?.Trim() ?? "",
            req.SourceNote ?? "Okuma", "A2", [item], ct), ct);

        logger.LogInformation("Okumadan kelime eklendi: {Lemma}, yeni kelime kartı: {Cards}", item.Lemma, result.NewWordCards);
        return new CaptureSaveResultDto { SentenceId = result.SentenceId, NewWordCards = result.NewWordCards, SentenceCard = result.SentenceCard };
    }

    private async Task<Row> LoadAsync(long id, CancellationToken ct)
    {
        await using var c = await db.OpenAsync(ct);
        return await c.QuerySingleOrDefaultAsync<Row>(new CommandDefinition(
            """
            SELECT Id, Title, Level, Topic, WordCount, ContentJson, CreatedAt, ReadAt
            FROM content.ReadingTexts WHERE Id = @id AND UserId = @userId
            """, new { id, userId = currentUser.Id }, cancellationToken: ct))
            ?? throw AppException.NotFound("Okuma metni bulunamadı.");
    }

    public static CefrLevel LevelFromVocabulary(int vocabulary) => vocabulary switch
    {
        < 250 => CefrLevel.A1,
        < 1000 => CefrLevel.A2,
        < 2500 => CefrLevel.B1,
        < 4000 => CefrLevel.B2,
        _ => CefrLevel.C1
    };

    /// <summary>Çekimli halden sözlük biçimi adayları: "stopped" → stopped, stop, stopp...; "cities" → city.</summary>
    public static List<string> CandidateLemmas(string word)
    {
        var set = new List<string> { word };
        void Add(string s) { if (s.Length >= 2 && !set.Contains(s)) set.Add(s); }
        if (word.EndsWith("'s")) Add(word[..^2]);
        if (word.EndsWith("ies")) Add(word[..^3] + "y");
        if (word.EndsWith("ied")) Add(word[..^3] + "y");
        if (word.EndsWith("es")) Add(word[..^2]);
        if (word.EndsWith('s')) Add(word[..^1]);
        if (word.EndsWith("ed")) { Add(word[..^2]); Add(word[..^1]); if (word.Length > 4 && word[^3] == word[^4]) Add(word[..^3]); }
        if (word.EndsWith("ing")) { Add(word[..^3]); Add(word[..^3] + "e"); if (word.Length > 5 && word[^4] == word[^5]) Add(word[..^4]); }
        if (word.EndsWith("er")) Add(word[..^2]);
        if (word.EndsWith("est")) Add(word[..^3]);
        if (word.EndsWith("ly")) Add(word[..^2]);
        return set;
    }

    private static ReadingContentDto Normalize(ReadingContentDto x)
    {
        x.Paragraphs = x.Paragraphs.Where(p => !string.IsNullOrWhiteSpace(p.En)).Take(8).ToList();
        x.Glossary = x.Glossary.Where(g => !string.IsNullOrWhiteSpace(g.Word) && !string.IsNullOrWhiteSpace(g.MeaningTr)).Take(10)
            .Select(g => { g.Lemma = string.IsNullOrWhiteSpace(g.Lemma) ? g.Word : g.Lemma; g.Pos = Enum.TryParse<PartOfSpeech>(g.Pos, true, out var p) ? p.ToString() : "Other"; return g; })
            .ToList();
        x.Questions = x.Questions.Where(q => q.Options.Count >= 2 && q.CorrectIndex >= 0 && q.CorrectIndex < q.Options.Count).Take(5).ToList();
        return x;
    }

    private static int CountWords(string s) => WordRegex().Matches(s).Count;

    [GeneratedRegex(@"[A-Za-z']+")]
    private static partial Regex WordRegex();

    private static string? Cut(string? s, int max) => s is null || s.Length <= max ? s : s[..max];
}

public sealed class ReadingRequestValidator : AbstractValidator<ReadingRequest>
{
    public ReadingRequestValidator()
    {
        RuleFor(x => x.Topic).MaximumLength(150).WithMessage("Konu en fazla 150 karakter olabilir.");
        RuleFor(x => x.Level).IsInEnum().When(x => x.Level.HasValue).WithMessage("Geçersiz seviye.");
    }
}

public sealed class WordLookupRequestValidator : AbstractValidator<WordLookupRequest>
{
    public WordLookupRequestValidator()
    {
        RuleFor(x => x.Word).NotEmpty().WithMessage("Kelime boş olamaz.").MaximumLength(60);
        RuleFor(x => x.Sentence).MaximumLength(600);
    }
}

public sealed class AddWordToCardsRequestValidator : AbstractValidator<AddWordToCardsRequest>
{
    public AddWordToCardsRequestValidator()
    {
        RuleFor(x => x.Lemma).NotEmpty().WithMessage("Kelime boş olamaz.").MaximumLength(100);
        RuleFor(x => x.MeaningTr).NotEmpty().WithMessage("Anlam boş olamaz.").MaximumLength(300);
        RuleFor(x => x.Sentence).NotEmpty().WithMessage("Cümle boş olamaz.").MaximumLength(500);
        RuleFor(x => x.SentenceTr).MaximumLength(500);
        RuleFor(x => x.SourceNote).MaximumLength(200);
        RuleFor(x => x.Ipa).MaximumLength(100);
    }
}
