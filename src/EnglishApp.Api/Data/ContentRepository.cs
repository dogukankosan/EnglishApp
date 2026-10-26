using Dapper;
using EnglishApp.Api.Infrastructure.Data;
using EnglishApp.Shared.Dtos;

namespace EnglishApp.Api.Data;

public static class StudyConstants
{
    /// <summary>Bu kalıcılığa (gün) ulaşan kart "öğrenildi" sayılır.</summary>
    public const double LearnedStabilityDays = 10;
    /// <summary>Bu kadar unutulan kart "zorlandıklarım" listesine düşer.</summary>
    public const int LeechLapses = 5;
}

public interface IContentRepository
{
    Task<IReadOnlyList<ScenarioListItemDto>> ListScenariosAsync(int userId, int? scenarioId = null, CancellationToken ct = default);
    Task<List<DialogueLineDto>> GetDialogueAsync(int scenarioId, CancellationToken ct = default);
    Task<List<WordDto>> GetScenarioWordsAsync(int scenarioId, CancellationToken ct = default);
}

public sealed class ContentRepository(IDbConnectionFactory db) : IContentRepository
{
    public async Task<IReadOnlyList<ScenarioListItemDto>> ListScenariosAsync(int userId, int? scenarioId = null, CancellationToken ct = default)
    {
        await using var c = await db.OpenAsync(ct);
        var rows = (await c.QueryAsync<ScenarioListItemDto>(new CommandDefinition(
            """
            WITH items AS (
                SELECT sw.ScenarioId, CAST(1 AS tinyint) AS Kind, sw.WordId AS ItemId
                FROM content.ScenarioWords sw
                JOIN content.Words w ON w.Id = sw.WordId AND w.Status = 1
                UNION ALL
                SELECT s.ScenarioId, CAST(3 AS tinyint), s.Id
                FROM content.Sentences s
                WHERE s.ScenarioId IS NOT NULL AND s.Status = 1
            )
            SELECT sc.Id, sc.Slug, sc.TitleTr, sc.TitleEn, sc.DescriptionTr, sc.Level, sc.CategoryId,
                   cat.NameTr AS CategoryName, cat.IconName,
                   COUNT(i.ItemId) AS TotalItems,
                   SUM(CASE WHEN cw.Id IS NOT NULL OR cs.Id IS NOT NULL OR k.WordId IS NOT NULL THEN 1 ELSE 0 END) AS StudiedItems,
                   SUM(CASE WHEN k.WordId IS NOT NULL
                              OR (cw.State = 2 AND cw.Stability >= @learned)
                              OR (cs.State = 2 AND cs.Stability >= @learned) THEN 1 ELSE 0 END) AS LearnedItems,
                   CAST(MAX(CASE WHEN p.UserId IS NOT NULL THEN 1 ELSE 0 END) AS bit) AS IsStarted
            FROM content.Scenarios sc
            JOIN content.ScenarioCategories cat ON cat.Id = sc.CategoryId
            LEFT JOIN items i ON i.ScenarioId = sc.Id
            LEFT JOIN study.Cards cw ON i.Kind = 1 AND cw.UserId = @userId AND cw.CardType = 1 AND cw.WordId = i.ItemId AND cw.SentenceId IS NULL
            LEFT JOIN study.Cards cs ON i.Kind = 3 AND cs.UserId = @userId AND cs.CardType = 3 AND cs.SentenceId = i.ItemId AND cs.WordId IS NULL
            LEFT JOIN study.UserKnownWords k ON i.Kind = 1 AND k.UserId = @userId AND k.WordId = i.ItemId
            LEFT JOIN study.UserScenarioProgress p ON p.UserId = @userId AND p.ScenarioId = sc.Id
            WHERE sc.Status = 1 AND (@scenarioId IS NULL OR sc.Id = @scenarioId)
            GROUP BY sc.Id, sc.Slug, sc.TitleTr, sc.TitleEn, sc.DescriptionTr, sc.Level, sc.CategoryId,
                     cat.NameTr, cat.IconName, cat.SortOrder, sc.SortOrder
            ORDER BY cat.SortOrder, sc.SortOrder, sc.Id
            """,
            new { userId, scenarioId, learned = StudyConstants.LearnedStabilityDays }, cancellationToken: ct))).ToList();

        foreach (var r in rows)
            r.ProgressPercent = r.TotalItems == 0 ? 0
                : (int)Math.Round(100.0 * (r.StudiedItems + r.LearnedItems) / (2.0 * r.TotalItems));
        return rows;
    }

    public async Task<List<DialogueLineDto>> GetDialogueAsync(int scenarioId, CancellationToken ct = default)
    {
        await using var c = await db.OpenAsync(ct);
        return (await c.QueryAsync<DialogueLineDto>(new CommandDefinition(
            """
            SELECT Id AS SentenceId, LineOrder, SpeakerRole, TextEn, TextTr
            FROM content.Sentences
            WHERE ScenarioId = @scenarioId AND Status = 1
            ORDER BY LineOrder
            """, new { scenarioId }, cancellationToken: ct))).ToList();
    }

    public async Task<List<WordDto>> GetScenarioWordsAsync(int scenarioId, CancellationToken ct = default)
    {
        await using var c = await db.OpenAsync(ct);
        return (await c.QueryAsync<WordDto>(new CommandDefinition(
            """
            SELECT w.Id, w.Lemma, w.PartOfSpeech, w.Level, w.Ipa, w.MeaningTr, w.DefinitionEn, w.UsageNoteTr
            FROM content.ScenarioWords sw
            JOIN content.Words w ON w.Id = sw.WordId AND w.Status = 1
            WHERE sw.ScenarioId = @scenarioId
            ORDER BY sw.SortOrder
            """, new { scenarioId }, cancellationToken: ct))).ToList();
    }
}
