using Dapper;
using EnglishApp.Api.Infrastructure.Data;
using EnglishApp.Shared.Common;
using EnglishApp.Shared.Enums;

namespace EnglishApp.Api.Data;

public sealed class WordCounts
{
    public int Learning { get; set; }
    public int Learned { get; set; }
    public int Struggling { get; set; }
    public int Known { get; set; }
}

public sealed class WordProgressRow
{
    public int WordId { get; set; }
    public string Lemma { get; set; } = "";
    public string MeaningTr { get; set; } = "";
    public CefrLevel Level { get; set; }
    public byte? State { get; set; }
    public float? Stability { get; set; }
    public int Reps { get; set; }
    public int Lapses { get; set; }
    public bool IsLeech { get; set; }
    public bool IsKnown { get; set; }
    public DateTime? DueAt { get; set; }
    public DateTime? LastReviewAt { get; set; }
}

public interface IStatsRepository
{
    Task<List<DailyStatsRow>> GetDailyStatsAsync(int userId, DateTime from, DateTime to, CancellationToken ct = default);
    Task<WordCounts> GetWordCountsAsync(int userId, CancellationToken ct = default);
    Task<int> GetPublishedWordCountAsync(CancellationToken ct = default);
    Task<PagedResult<WordProgressRow>> GetWordProgressAsync(int userId, WordProgressFilter filter, string? search, int page, int pageSize, CancellationToken ct = default);
    Task<List<WordProgressRow>> GetStrugglingWordsAsync(int userId, int top, CancellationToken ct = default);
    Task<int> CountCardsCreatedAsync(int userId, CardType type, DateTime from, DateTime to, CancellationToken ct = default);
}

public sealed class StatsRepository(IDbConnectionFactory db) : IStatsRepository
{
    public async Task<List<DailyStatsRow>> GetDailyStatsAsync(int userId, DateTime from, DateTime to, CancellationToken ct = default)
    {
        await using var c = await db.OpenAsync(ct);
        return (await c.QueryAsync<DailyStatsRow>(new CommandDefinition(
            """
            SELECT StatDate, NewCount, ReviewCount, CorrectCount, StudySeconds, AiTokens, AiMessages
            FROM study.DailyStats
            WHERE UserId = @userId AND StatDate BETWEEN @from AND @to
            ORDER BY StatDate
            """, new { userId, from = from.Date, to = to.Date }, cancellationToken: ct))).ToList();
    }

    public async Task<WordCounts> GetWordCountsAsync(int userId, CancellationToken ct = default)
    {
        await using var c = await db.OpenAsync(ct);
        return await c.QuerySingleAsync<WordCounts>(new CommandDefinition(
            """
            SELECT
                ISNULL(SUM(CASE WHEN c.IsLeech = 0 AND NOT (c.State = 2 AND c.Stability >= @learned) THEN 1 ELSE 0 END), 0) AS Learning,
                ISNULL(SUM(CASE WHEN c.IsLeech = 0 AND c.State = 2 AND c.Stability >= @learned THEN 1 ELSE 0 END), 0) AS Learned,
                ISNULL(SUM(CASE WHEN c.IsLeech = 1 THEN 1 ELSE 0 END), 0) AS Struggling,
                (SELECT COUNT(*) FROM study.UserKnownWords k
                 WHERE k.UserId = @userId
                   AND NOT EXISTS (SELECT 1 FROM study.Cards x WHERE x.UserId = @userId AND x.CardType = 1 AND x.WordId = k.WordId)) AS Known
            FROM study.Cards c
            WHERE c.UserId = @userId AND c.CardType = 1 AND c.WordId IS NOT NULL
            """, new { userId, learned = StudyConstants.LearnedStabilityDays }, cancellationToken: ct));
    }

    public async Task<int> GetPublishedWordCountAsync(CancellationToken ct = default)
    {
        await using var c = await db.OpenAsync(ct);
        return await c.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(*) FROM content.Words WHERE Status = 1 AND OwnerUserId IS NULL", cancellationToken: ct));
    }

    public async Task<PagedResult<WordProgressRow>> GetWordProgressAsync(int userId, WordProgressFilter filter, string? search,
        int page, int pageSize, CancellationToken ct = default)
    {
        // filter: sabit SQL parçası (kullanıcı metni değil)
        var filterSql = filter switch
        {
            WordProgressFilter.Learning => "AND x.IsKnown = 0 AND x.IsLeech = 0 AND NOT (x.State = 2 AND x.Stability >= @learned)",
            WordProgressFilter.Learned => "AND x.IsKnown = 0 AND x.IsLeech = 0 AND x.State = 2 AND x.Stability >= @learned",
            WordProgressFilter.Struggling => "AND x.IsLeech = 1",
            WordProgressFilter.Known => "AND x.IsKnown = 1",
            _ => ""
        };

        var sql = $"""
            WITH x AS (
                SELECT w.Id AS WordId, w.Lemma, w.MeaningTr, w.Level, c.State, c.Stability, c.Reps, c.Lapses,
                       c.IsLeech, CAST(0 AS bit) AS IsKnown, c.DueAt, c.LastReviewAt
                FROM study.Cards c
                JOIN content.Words w ON w.Id = c.WordId
                WHERE c.UserId = @userId AND c.CardType = 1
                UNION ALL
                SELECT w.Id, w.Lemma, w.MeaningTr, w.Level, NULL, NULL, 0, 0, CAST(0 AS bit), CAST(1 AS bit), NULL, k.MarkedAt
                FROM study.UserKnownWords k
                JOIN content.Words w ON w.Id = k.WordId
                WHERE k.UserId = @userId
                  AND NOT EXISTS (SELECT 1 FROM study.Cards c2 WHERE c2.UserId = @userId AND c2.CardType = 1 AND c2.WordId = k.WordId)
            )
            SELECT COUNT(*) FROM x WHERE (@search IS NULL OR x.Lemma LIKE @search OR x.MeaningTr LIKE @search) {filterSql};

            WITH x AS (
                SELECT w.Id AS WordId, w.Lemma, w.MeaningTr, w.Level, c.State, c.Stability, c.Reps, c.Lapses,
                       c.IsLeech, CAST(0 AS bit) AS IsKnown, c.DueAt, c.LastReviewAt
                FROM study.Cards c
                JOIN content.Words w ON w.Id = c.WordId
                WHERE c.UserId = @userId AND c.CardType = 1
                UNION ALL
                SELECT w.Id, w.Lemma, w.MeaningTr, w.Level, NULL, NULL, 0, 0, CAST(0 AS bit), CAST(1 AS bit), NULL, k.MarkedAt
                FROM study.UserKnownWords k
                JOIN content.Words w ON w.Id = k.WordId
                WHERE k.UserId = @userId
                  AND NOT EXISTS (SELECT 1 FROM study.Cards c2 WHERE c2.UserId = @userId AND c2.CardType = 1 AND c2.WordId = k.WordId)
            )
            SELECT * FROM x
            WHERE (@search IS NULL OR x.Lemma LIKE @search OR x.MeaningTr LIKE @search) {filterSql}
            ORDER BY x.IsLeech DESC, x.Lapses DESC, x.LastReviewAt DESC, x.Lemma
            OFFSET @offset ROWS FETCH NEXT @pageSize ROWS ONLY;
            """;

        await using var c = await db.OpenAsync(ct);
        using var multi = await c.QueryMultipleAsync(new CommandDefinition(sql, new
        {
            userId,
            learned = StudyConstants.LearnedStabilityDays,
            search = string.IsNullOrWhiteSpace(search) ? null : $"%{search.Trim()}%",
            offset = (page - 1) * pageSize,
            pageSize
        }, cancellationToken: ct));

        var total = await multi.ReadSingleAsync<int>();
        var items = (await multi.ReadAsync<WordProgressRow>()).ToList();
        return new PagedResult<WordProgressRow>(items, total, page, pageSize);
    }

    public async Task<List<WordProgressRow>> GetStrugglingWordsAsync(int userId, int top, CancellationToken ct = default)
    {
        await using var c = await db.OpenAsync(ct);
        return (await c.QueryAsync<WordProgressRow>(new CommandDefinition(
            """
            SELECT TOP (@top) w.Id AS WordId, w.Lemma, w.MeaningTr, w.Level, c.State, c.Stability, c.Reps, c.Lapses,
                   c.IsLeech, CAST(0 AS bit) AS IsKnown, c.DueAt, c.LastReviewAt
            FROM study.Cards c
            JOIN content.Words w ON w.Id = c.WordId
            WHERE c.UserId = @userId AND c.CardType = 1 AND c.Lapses > 0
            ORDER BY c.IsLeech DESC, c.Lapses DESC, c.Difficulty DESC
            """, new { userId, top }, cancellationToken: ct))).ToList();
    }

    public async Task<int> CountCardsCreatedAsync(int userId, CardType type, DateTime from, DateTime to, CancellationToken ct = default)
    {
        await using var c = await db.OpenAsync(ct);
        return await c.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(*) FROM study.Cards WHERE UserId = @userId AND CardType = @type AND CreatedAt >= @from AND CreatedAt < @to",
            new { userId, type = (byte)type, from, to }, cancellationToken: ct));
    }
}
