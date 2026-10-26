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

/// <summary>Gidilecek yere özel kalıp listesi (AI) + tek tıkla kartlara ekleme.</summary>
public interface ITravelKitService
{
    Task<TravelKitDto> CreateAsync(TravelKitRequest req, CancellationToken ct);
    Task<IReadOnlyList<TravelKitListItemDto>> ListAsync(CancellationToken ct);
    Task<TravelKitDto> GetAsync(long id, CancellationToken ct);
    Task<int> AddToCardsAsync(long id, CancellationToken ct);
    Task DeleteAsync(long id, CancellationToken ct);
}

public sealed class TravelKitService(
    IDbConnectionFactory db,
    ILlmService llm,
    PersonalContentWriter writer,
    ICurrentUser currentUser,
    ITrClock clock,
    ILogger<TravelKitService> logger) : ITravelKitService
{
    private sealed class Row
    {
        public long Id { get; set; }
        public string Title { get; set; } = "";
        public string Country { get; set; } = "";
        public string? City { get; set; }
        public string? Purpose { get; set; }
        public byte? Days { get; set; }
        public string ContentJson { get; set; } = "";
        public bool AddedToCards { get; set; }
        public DateTime CreatedAt { get; set; }
    }

    public async Task<TravelKitDto> CreateAsync(TravelKitRequest req, CancellationToken ct)
    {
        req.Country = req.Country.Trim();
        req.City = string.IsNullOrWhiteSpace(req.City) ? null : req.City.Trim();

        var r = await llm.GenerateJsonAsync<TravelKitContentDto>(currentUser.Id, AiFeature.ContentGeneration,
            [LlmMessage.System(PracticePrompts.TravelSystem()), LlmMessage.User(PracticePrompts.TravelUser(req))],
            PracticePrompts.TravelSchema, 0.5, k => k.Sections.Count >= 3 && k.Sections.Sum(s => s.Phrases.Count) >= 12, ct);

        var content = Normalize(r.Data);
        var title = string.IsNullOrWhiteSpace(content.TitleTr) ? $"{req.City ?? req.Country} Seyahat Kiti" : content.TitleTr.Trim();

        await using var c = await db.OpenAsync(ct);
        var id = await c.ExecuteScalarAsync<long>(new CommandDefinition(
            """
            INSERT INTO ai.TravelKits (UserId, Title, Country, City, Purpose, Days, ContentJson, CreatedAt)
            VALUES (@userId, @title, @country, @city, @purpose, @days, @json, @now);
            SELECT CAST(SCOPE_IDENTITY() AS bigint);
            """,
            new
            {
                userId = currentUser.Id, title = Cut(title, 150), country = Cut(req.Country, 80), city = Cut(req.City, 80),
                purpose = Cut(req.Purpose, 80), days = req.Days is null ? null : (byte?)Math.Clamp(req.Days.Value, 1, 255),
                json = JsonSerializer.Serialize(content, JsonDefaults.Options), now = clock.Now
            }, cancellationToken: ct));

        logger.LogInformation("Seyahat kiti oluşturuldu: {KitId} {Country}/{City}, {Phrases} kalıp", id, req.Country, req.City,
            content.Sections.Sum(s => s.Phrases.Count));
        return await GetAsync(id, ct);
    }

    public async Task<IReadOnlyList<TravelKitListItemDto>> ListAsync(CancellationToken ct)
    {
        await using var c = await db.OpenAsync(ct);
        return (await c.QueryAsync<TravelKitListItemDto>(new CommandDefinition(
            """
            SELECT TOP (30) Id, Title, Country, City, AddedToCards, CreatedAt
            FROM ai.TravelKits WHERE UserId = @userId ORDER BY CreatedAt DESC
            """, new { userId = currentUser.Id }, cancellationToken: ct))).ToList();
    }

    public async Task<TravelKitDto> GetAsync(long id, CancellationToken ct)
    {
        var row = await LoadAsync(id, ct);
        return new TravelKitDto
        {
            Id = row.Id, Title = row.Title, Country = row.Country, City = row.City, Purpose = row.Purpose, Days = row.Days,
            AddedToCards = row.AddedToCards, CreatedAt = row.CreatedAt,
            Content = JsonSerializer.Deserialize<TravelKitContentDto>(row.ContentJson, JsonDefaults.Options) ?? new()
        };
    }

    /// <summary>Kitteki tüm kalıpları kişisel cümle kartı olarak ekler (dinle-anla kartı).</summary>
    public async Task<int> AddToCardsAsync(long id, CancellationToken ct)
    {
        var kit = await GetAsync(id, ct);
        if (kit.AddedToCards) throw new AppException("ALREADY_ADDED", "Bu kitin kalıpları zaten kartlarına eklendi.");

        var userId = currentUser.Id;
        var phrases = kit.Content.Sections.SelectMany(s => s.Phrases).Where(p => !string.IsNullOrWhiteSpace(p.En)).Take(45).ToList();
        var added = await db.InTransactionAsync(async (c, tx) =>
        {
            var count = 0;
            foreach (var p in phrases)
            {
                var r = await writer.SaveAsync(c, tx, userId, p.En.Trim(), p.Tr.Trim(), $"Seyahat kiti: {kit.Title}", "A2", [], ct);
                if (r.SentenceCard) count++;
            }
            await c.ExecuteAsync(new CommandDefinition(
                "UPDATE ai.TravelKits SET AddedToCards = 1 WHERE Id = @id AND UserId = @userId", new { id, userId }, tx, cancellationToken: ct));
            return count;
        }, ct);

        logger.LogInformation("Seyahat kiti kartlara eklendi: {KitId}, {Count} kart", id, added);
        return added;
    }

    public async Task DeleteAsync(long id, CancellationToken ct)
    {
        _ = await LoadAsync(id, ct);
        await using var c = await db.OpenAsync(ct);
        await c.ExecuteAsync(new CommandDefinition(
            "DELETE FROM ai.TravelKits WHERE Id = @id AND UserId = @userId", new { id, userId = currentUser.Id }, cancellationToken: ct));
    }

    private async Task<Row> LoadAsync(long id, CancellationToken ct)
    {
        await using var c = await db.OpenAsync(ct);
        return await c.QuerySingleOrDefaultAsync<Row>(new CommandDefinition(
            """
            SELECT Id, Title, Country, City, Purpose, Days, ContentJson, AddedToCards, CreatedAt
            FROM ai.TravelKits WHERE Id = @id AND UserId = @userId
            """, new { id, userId = currentUser.Id }, cancellationToken: ct))
            ?? throw AppException.NotFound("Seyahat kiti bulunamadı.");
    }

    private static TravelKitContentDto Normalize(TravelKitContentDto k)
    {
        k.LocalTipsTr = k.LocalTipsTr.Where(t => !string.IsNullOrWhiteSpace(t)).Take(8).ToList();
        k.Sections = k.Sections.Where(s => s.Phrases.Count > 0).Take(9).Select(s =>
        {
            s.Icon = PracticePrompts.TravelIcons.Contains(s.Icon) ? s.Icon : "chat-dots";
            s.Phrases = s.Phrases.Where(p => !string.IsNullOrWhiteSpace(p.En)).Take(8)
                .Select(p => { p.NoteTr = string.IsNullOrWhiteSpace(p.NoteTr) ? null : p.NoteTr; return p; }).ToList();
            return s;
        }).ToList();
        return k;
    }

    private static string? Cut(string? s, int max) => s is null || s.Length <= max ? s : s[..max];
}

public sealed class TravelKitRequestValidator : AbstractValidator<TravelKitRequest>
{
    public TravelKitRequestValidator()
    {
        RuleFor(x => x.Country).NotEmpty().WithMessage("Ülke boş olamaz.").MaximumLength(80);
        RuleFor(x => x.City).MaximumLength(80);
        RuleFor(x => x.Purpose).MaximumLength(80);
        RuleFor(x => x.Days).InclusiveBetween(1, 120).When(x => x.Days.HasValue).WithMessage("Gün sayısı 1-120 arasında olmalı.");
        RuleFor(x => x.Notes).MaximumLength(300);
    }
}
