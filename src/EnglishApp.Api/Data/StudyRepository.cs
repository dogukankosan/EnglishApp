using Dapper;
using EnglishApp.Api.Infrastructure.Data;
using EnglishApp.Shared.Dtos;
using EnglishApp.Shared.Enums;
using Microsoft.Data.SqlClient;

namespace EnglishApp.Api.Data;

public sealed class CardRow
{
    public long Id { get; set; }
    public int UserId { get; set; }
    public CardType CardType { get; set; }
    public int? WordId { get; set; }
    public int? SentenceId { get; set; }
    public CardState State { get; set; }
    public float Stability { get; set; }
    public float Difficulty { get; set; }
    public byte Step { get; set; }
    public int Reps { get; set; }
    public int Lapses { get; set; }
    public DateTime DueAt { get; set; }
    public DateTime? LastReviewAt { get; set; }
    public bool IsLeech { get; set; }
}

public sealed class DailyStatsRow
{
    public DateTime StatDate { get; set; }
    public short NewCount { get; set; }
    public short ReviewCount { get; set; }
    public short CorrectCount { get; set; }
    public int StudySeconds { get; set; }
    public int AiTokens { get; set; }
    public short AiMessages { get; set; }
}

public sealed class ExampleRow
{
    public int WordId { get; set; }
    public string TextEn { get; set; } = "";
    public string TextTr { get; set; } = "";
    public short CharStart { get; set; }
    public short CharLength { get; set; }
}

public sealed record ReviewSave(
    int UserId,
    long? CardId,
    CardType CardType,
    int? WordId,
    int? SentenceId,
    CardState StateBefore,
    CardState State,
    double Stability,
    double Difficulty,
    int Step,
    int Reps,
    int Lapses,
    DateTime DueAt,
    DateTime ReviewedAt,
    bool IsLeech,
    Rating Rating,
    double ElapsedDays,
    double ScheduledDays,
    int? DurationMs,
    DateTime StatDate,
    bool IsNewCard);

public interface IStudyRepository
{
    Task<List<CardRow>> GetDueCardsAsync(int userId, DateTime now, DateTime endOfDay, int limit, int? scenarioId, CancellationToken ct = default);
    Task<int> CountDueAsync(int userId, DateTime now, DateTime endOfDay, CancellationToken ct = default);
    Task<List<int>> GetNewWordIdsAsync(int userId, int? scenarioId, int limit, CancellationToken ct = default);
    Task<List<int>> GetNewSentenceIdsAsync(int userId, int? scenarioId, int limit, CancellationToken ct = default);
    Task<List<int>> GetNewGeneralWordIdsAsync(int userId, int limit, IReadOnlyCollection<int> exclude, CancellationToken ct = default);
    Task<(List<WordDto> Words, List<DialogueLineDto> Sentences, List<ExampleRow> Examples)> LoadContentAsync(
        IReadOnlyCollection<int> wordIds, IReadOnlyCollection<int> sentenceIds, CancellationToken ct = default, int? userId = null);
    Task<CardRow?> GetCardAsync(int userId, long cardId, CancellationToken ct = default);
    Task<CardRow?> GetCardByTargetAsync(int userId, CardType type, int? wordId, int? sentenceId, CancellationToken ct = default);
    /// <summary>Kart açılabilir mi: içerik yayında ve ortak ya da bu kullanıcının kişisel içeriği.</summary>
    Task<bool> ContentExistsAsync(int userId, int? wordId, int? sentenceId, CancellationToken ct = default);
    Task<long> SaveReviewAsync(ReviewSave s, CancellationToken ct = default);
    Task<DailyStatsRow?> GetDailyStatsAsync(int userId, DateTime date, CancellationToken ct = default);
    Task TouchScenarioAsync(int userId, int scenarioId, DateTime now, CancellationToken ct = default);
}

public sealed class StudyRepository(IDbConnectionFactory db) : IStudyRepository
{
    private const string CardColumns =
        "c.Id, c.UserId, c.CardType, c.WordId, c.SentenceId, c.State, c.Stability, c.Difficulty, c.Step, c.Reps, c.Lapses, c.DueAt, c.LastReviewAt, c.IsLeech";

    // Öğrenme adımındaki kartlar zamanı gelince, Review kartları gün içinde herhangi bir saatte gelir
    private const string DueCondition = "(c.DueAt <= @now OR (c.State = 2 AND c.DueAt < @endOfDay))";

    public async Task<List<CardRow>> GetDueCardsAsync(int userId, DateTime now, DateTime endOfDay, int limit, int? scenarioId, CancellationToken ct = default)
    {
        if (limit <= 0) return [];
        await using var c = await db.OpenAsync(ct);
        var sql = scenarioId is null
            ? $"""
               SELECT TOP (@limit) {CardColumns}
               FROM study.Cards c
               WHERE c.UserId = @userId AND c.IsSuspended = 0 AND {DueCondition}
               ORDER BY c.DueAt
               """
            : $"""
               SELECT TOP (@limit) {CardColumns}
               FROM study.Cards c
               WHERE c.UserId = @userId AND c.IsSuspended = 0 AND {DueCondition}
                 AND (EXISTS (SELECT 1 FROM content.ScenarioWords sw WHERE sw.ScenarioId = @scenarioId AND sw.WordId = c.WordId)
                   OR EXISTS (SELECT 1 FROM content.Sentences s WHERE s.ScenarioId = @scenarioId AND s.Id = c.SentenceId))
               ORDER BY c.DueAt
               """;
        return (await c.QueryAsync<CardRow>(new CommandDefinition(sql,
            new { userId, now, endOfDay, limit, scenarioId }, cancellationToken: ct))).ToList();
    }

    public async Task<int> CountDueAsync(int userId, DateTime now, DateTime endOfDay, CancellationToken ct = default)
    {
        await using var c = await db.OpenAsync(ct);
        return await c.ExecuteScalarAsync<int>(new CommandDefinition(
            $"SELECT COUNT(*) FROM study.Cards c WHERE c.UserId = @userId AND c.IsSuspended = 0 AND {DueCondition}",
            new { userId, now, endOfDay }, cancellationToken: ct));
    }

    public async Task<List<int>> GetNewWordIdsAsync(int userId, int? scenarioId, int limit, CancellationToken ct = default)
    {
        if (limit <= 0) return [];
        await using var c = await db.OpenAsync(ct);
        // Başlanmış senaryoların kelimeleri önce, sonra senaryo sırası, sonra senaryodaki kelime sırası
        return (await c.QueryAsync<int>(new CommandDefinition(
            """
            SELECT TOP (@limit) w.Id
            FROM content.ScenarioWords sw
            JOIN content.Scenarios sc ON sc.Id = sw.ScenarioId AND sc.Status = 1
            JOIN content.Words w ON w.Id = sw.WordId AND w.Status = 1
            LEFT JOIN study.UserScenarioProgress p ON p.UserId = @userId AND p.ScenarioId = sc.Id
            WHERE (@scenarioId IS NULL OR sw.ScenarioId = @scenarioId)
              AND NOT EXISTS (SELECT 1 FROM study.Cards c
                              WHERE c.UserId = @userId AND c.CardType = 1 AND c.WordId = w.Id AND c.SentenceId IS NULL)
              AND NOT EXISTS (SELECT 1 FROM study.UserKnownWords k WHERE k.UserId = @userId AND k.WordId = w.Id)
            GROUP BY w.Id
            ORDER BY MIN(CASE WHEN p.UserId IS NULL THEN 1 ELSE 0 END), MIN(sc.SortOrder), MIN(sc.Id), MIN(sw.SortOrder)
            """, new { userId, scenarioId, limit }, cancellationToken: ct))).ToList();
    }

    /// <summary>Genel kelime havuzu (içe aktarılan sıklık listesi): senaryo kelimeleri bitince sıklık sırasıyla.</summary>
    public async Task<List<int>> GetNewGeneralWordIdsAsync(int userId, int limit, IReadOnlyCollection<int> exclude, CancellationToken ct = default)
    {
        if (limit <= 0) return [];
        var ex = exclude.Count == 0 ? [-1] : exclude.ToArray();
        await using var c = await db.OpenAsync(ct);
        return (await c.QueryAsync<int>(new CommandDefinition(
            """
            SELECT TOP (@limit) w.Id
            FROM content.Words w
            WHERE w.OwnerUserId IS NULL AND w.FrequencyRank IS NOT NULL AND w.Status = 1 AND w.Id NOT IN @ex
              AND NOT EXISTS (SELECT 1 FROM study.Cards c WHERE c.UserId = @userId AND c.CardType = 1 AND c.WordId = w.Id AND c.SentenceId IS NULL)
              AND NOT EXISTS (SELECT 1 FROM study.UserKnownWords k WHERE k.UserId = @userId AND k.WordId = w.Id)
            ORDER BY w.FrequencyRank
            """, new { userId, limit, ex }, cancellationToken: ct))).ToList();
    }

    public async Task<List<int>> GetNewSentenceIdsAsync(int userId, int? scenarioId, int limit, CancellationToken ct = default)
    {
        if (limit <= 0) return [];
        await using var c = await db.OpenAsync(ct);
        // Senaryo seçilmediyse sadece başlanmış senaryoların diyalog cümleleri
        return (await c.QueryAsync<int>(new CommandDefinition(
            """
            SELECT TOP (@limit) s.Id
            FROM content.Sentences s
            JOIN content.Scenarios sc ON sc.Id = s.ScenarioId AND sc.Status = 1
            WHERE s.Status = 1
              AND ((@scenarioId IS NOT NULL AND s.ScenarioId = @scenarioId)
                OR (@scenarioId IS NULL AND EXISTS (SELECT 1 FROM study.UserScenarioProgress p
                                                    WHERE p.UserId = @userId AND p.ScenarioId = s.ScenarioId)))
              AND NOT EXISTS (SELECT 1 FROM study.Cards c
                              WHERE c.UserId = @userId AND c.CardType = 3 AND c.SentenceId = s.Id AND c.WordId IS NULL)
            ORDER BY sc.SortOrder, sc.Id, s.LineOrder
            """, new { userId, scenarioId, limit }, cancellationToken: ct))).ToList();
    }

    public async Task<(List<WordDto> Words, List<DialogueLineDto> Sentences, List<ExampleRow> Examples)> LoadContentAsync(
        IReadOnlyCollection<int> wordIds, IReadOnlyCollection<int> sentenceIds, CancellationToken ct = default, int? userId = null)
    {
        if (wordIds.Count == 0 && sentenceIds.Count == 0) return ([], [], []);

        // Dapper "IN @ids" boş listede sorun çıkarmasın diye -1 ekleniyor
        var wIds = wordIds.Count == 0 ? [-1] : wordIds.ToArray();
        var sIds = sentenceIds.Count == 0 ? [-1] : sentenceIds.ToArray();

        await using var c = await db.OpenAsync(ct);
        using var multi = await c.QueryMultipleAsync(new CommandDefinition(
            """
            SELECT Id, Lemma, PartOfSpeech, Level, Ipa, MeaningTr, DefinitionEn, UsageNoteTr
            FROM content.Words WHERE Id IN @wIds;

            SELECT Id AS SentenceId, ScenarioId, LineOrder, SpeakerRole, TextEn, TextTr
            FROM content.Sentences WHERE Id IN @sIds;

            SELECT x.WordId, x.TextEn, x.TextTr, x.CharStart, x.CharLength
            FROM (
                SELECT sw.WordId, s.TextEn, s.TextTr, sw.CharStart, sw.CharLength,
                       ROW_NUMBER() OVER (PARTITION BY sw.WordId
                                          ORDER BY CASE WHEN s.ScenarioId IS NULL THEN 0 ELSE 1 END, s.Id) AS rn
                FROM content.SentenceWords sw
                JOIN content.Sentences s ON s.Id = sw.SentenceId AND s.Status = 1
                WHERE sw.WordId IN @wIds
                  AND (s.OwnerUserId IS NULL OR s.OwnerUserId = @userId) -- başkasının kişisel cümlesi örnek olmasın
            ) x
            WHERE x.rn = 1;
            """, new { wIds, sIds, userId }, cancellationToken: ct));

        var words = (await multi.ReadAsync<WordDto>()).ToList();
        var sentences = (await multi.ReadAsync<DialogueLineDto>()).ToList();
        var examples = (await multi.ReadAsync<ExampleRow>()).ToList();
        return (words, sentences, examples);
    }

    public async Task<CardRow?> GetCardAsync(int userId, long cardId, CancellationToken ct = default)
    {
        await using var c = await db.OpenAsync(ct);
        return await c.QuerySingleOrDefaultAsync<CardRow>(new CommandDefinition(
            $"SELECT {CardColumns} FROM study.Cards c WHERE c.Id = @cardId AND c.UserId = @userId",
            new { userId, cardId }, cancellationToken: ct));
    }

    public async Task<CardRow?> GetCardByTargetAsync(int userId, CardType type, int? wordId, int? sentenceId, CancellationToken ct = default)
    {
        await using var c = await db.OpenAsync(ct);
        return await c.QuerySingleOrDefaultAsync<CardRow>(new CommandDefinition(
            $"""
             SELECT {CardColumns} FROM study.Cards c
             WHERE c.UserId = @userId AND c.CardType = @type
               AND ((@wordId IS NULL AND c.WordId IS NULL) OR c.WordId = @wordId)
               AND ((@sentenceId IS NULL AND c.SentenceId IS NULL) OR c.SentenceId = @sentenceId)
             """, new { userId, type = (byte)type, wordId, sentenceId }, cancellationToken: ct));
    }

    public async Task<bool> ContentExistsAsync(int userId, int? wordId, int? sentenceId, CancellationToken ct = default)
    {
        if (wordId is null && sentenceId is null) return false;
        await using var c = await db.OpenAsync(ct);
        // Başka kullanıcının kişisel kelime/cümlesine kart açılamaz (içeriği görülmesin)
        return await c.ExecuteScalarAsync<bool>(new CommandDefinition(
            """
            SELECT CASE WHEN (@wordId IS NULL OR EXISTS (SELECT 1 FROM content.Words WHERE Id = @wordId AND Status = 1
                                                         AND (OwnerUserId IS NULL OR OwnerUserId = @userId)))
                         AND (@sentenceId IS NULL OR EXISTS (SELECT 1 FROM content.Sentences WHERE Id = @sentenceId AND Status = 1
                                                             AND (OwnerUserId IS NULL OR OwnerUserId = @userId)))
                   THEN 1 ELSE 0 END
            """, new { userId, wordId, sentenceId }, cancellationToken: ct));
    }

    /// <summary>
    /// Bir cevabın tüm yazma işlemleri TEK transaction'da:
    /// kart (yoksa oluştur) + kart güncelle + tekrar kaydı + günlük özet.
    /// </summary>
    public Task<long> SaveReviewAsync(ReviewSave s, CancellationToken ct = default) =>
        db.InTransactionAsync(async (c, tx) =>
        {
            long cardId;
            if (s.CardId is null)
            {
                // Aynı anda iki istek gelirse tekil index'e takılmasın: kilitle ve kontrol et
                var existing = await c.ExecuteScalarAsync<long?>(new CommandDefinition(
                    """
                    SELECT Id FROM study.Cards WITH (UPDLOCK, HOLDLOCK)
                    WHERE UserId = @UserId AND CardType = @CardType
                      AND ((@WordId IS NULL AND WordId IS NULL) OR WordId = @WordId)
                      AND ((@SentenceId IS NULL AND SentenceId IS NULL) OR SentenceId = @SentenceId)
                    """, new { s.UserId, CardType = (byte)s.CardType, s.WordId, s.SentenceId }, tx, cancellationToken: ct));

                cardId = existing ?? await c.ExecuteScalarAsync<long>(new CommandDefinition(
                    """
                    INSERT INTO study.Cards (UserId, CardType, WordId, SentenceId, DueAt, CreatedAt)
                    VALUES (@UserId, @CardType, @WordId, @SentenceId, @ReviewedAt, @ReviewedAt);
                    SELECT CAST(SCOPE_IDENTITY() AS bigint);
                    """, new { s.UserId, CardType = (byte)s.CardType, s.WordId, s.SentenceId, s.ReviewedAt }, tx, cancellationToken: ct));
            }
            else
            {
                cardId = s.CardId.Value;
            }

            await c.ExecuteAsync(new CommandDefinition(
                """
                UPDATE study.Cards
                SET State = @State, Stability = @Stability, Difficulty = @Difficulty, Step = @Step,
                    Reps = @Reps, Lapses = @Lapses, DueAt = @DueAt, LastReviewAt = @ReviewedAt, IsLeech = @IsLeech
                WHERE Id = @cardId AND UserId = @UserId;

                INSERT INTO study.ReviewLogs (CardId, UserId, Rating, StateBefore, StabilityAfter, DifficultyAfter,
                                              ElapsedDays, ScheduledDays, DurationMs, ReviewedAt)
                VALUES (@cardId, @UserId, @Rating, @StateBefore, @Stability, @Difficulty,
                        @ElapsedDays, @ScheduledDays, @DurationMs, @ReviewedAt);

                UPDATE study.DailyStats WITH (UPDLOCK, SERIALIZABLE)
                SET NewCount = NewCount + @NewInc, ReviewCount = ReviewCount + 1,
                    CorrectCount = CorrectCount + @CorrectInc, StudySeconds = StudySeconds + @Seconds
                WHERE UserId = @UserId AND StatDate = @StatDate;

                IF @@ROWCOUNT = 0
                    INSERT INTO study.DailyStats (UserId, StatDate, NewCount, ReviewCount, CorrectCount, StudySeconds)
                    VALUES (@UserId, @StatDate, @NewInc, 1, @CorrectInc, @Seconds);
                """,
                new
                {
                    cardId, s.UserId,
                    State = (byte)s.State, Stability = (float)s.Stability, Difficulty = (float)s.Difficulty,
                    Step = (byte)Math.Min(s.Step, 255), s.Reps, s.Lapses, s.DueAt, s.ReviewedAt, s.IsLeech,
                    Rating = (byte)s.Rating, StateBefore = (byte)s.StateBefore,
                    ElapsedDays = (float)s.ElapsedDays, ScheduledDays = (float)s.ScheduledDays, s.DurationMs,
                    s.StatDate,
                    NewInc = s.IsNewCard ? 1 : 0,
                    CorrectInc = s.Rating >= Rating.Good ? 1 : 0,
                    // Takılıp kalınan kartlar istatistiği şişirmesin: kart başına en fazla 60 sn
                    Seconds = Math.Clamp((s.DurationMs ?? 0) / 1000, 0, 60)
                }, tx, cancellationToken: ct));

            return cardId;
        }, ct);

    public async Task<DailyStatsRow?> GetDailyStatsAsync(int userId, DateTime date, CancellationToken ct = default)
    {
        await using var c = await db.OpenAsync(ct);
        return await c.QuerySingleOrDefaultAsync<DailyStatsRow>(new CommandDefinition(
            """
            SELECT StatDate, NewCount, ReviewCount, CorrectCount, StudySeconds, AiTokens, AiMessages
            FROM study.DailyStats WHERE UserId = @userId AND StatDate = @date
            """, new { userId, date = date.Date }, cancellationToken: ct));
    }

    public async Task TouchScenarioAsync(int userId, int scenarioId, DateTime now, CancellationToken ct = default)
    {
        await using var c = await db.OpenAsync(ct);
        await c.ExecuteAsync(new CommandDefinition(
            """
            UPDATE study.UserScenarioProgress WITH (UPDLOCK, SERIALIZABLE)
            SET LastStudiedAt = @now WHERE UserId = @userId AND ScenarioId = @scenarioId;
            IF @@ROWCOUNT = 0
                INSERT INTO study.UserScenarioProgress (UserId, ScenarioId, StartedAt, LastStudiedAt)
                VALUES (@userId, @scenarioId, @now, @now);
            """, new { userId, scenarioId, now }, cancellationToken: ct));
    }
}
