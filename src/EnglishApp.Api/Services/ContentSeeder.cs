using System.Text.Json;
using System.Text.RegularExpressions;
using Dapper;
using EnglishApp.Api.Infrastructure;
using EnglishApp.Api.Infrastructure.Data;
using EnglishApp.Api.Infrastructure.Time;
using EnglishApp.Shared.Enums;
using Microsoft.Data.SqlClient;

namespace EnglishApp.Api.Services;

public interface IContentSeeder
{
    Task SeedAsync(CancellationToken ct = default);
    Task<int> ImportScenarioAsync(SqlConnection c, ContentSeeder.SeedScenario s, ContentStatus status, int? createdBy, bool isAiGenerated, CancellationToken ct);
}

/// <summary>
/// Content/seed-content.json içindeki senaryoları DB'ye yükler.
/// Slug'ı zaten olan senaryo atlanır (tekrar çalıştırmak güvenli).
/// Kelimelerin cümle içindeki konumları otomatik bulunur (vurgulama ve boşluk doldurma için).
/// </summary>
public sealed class ContentSeeder(IDbConnectionFactory db, IWebHostEnvironment env, ITrClock clock, ILogger<ContentSeeder> logger) : IContentSeeder
{
    public sealed class SeedFile { public List<SeedScenario> Scenarios { get; set; } = []; }

    public sealed class SeedScenario
    {
        public string Slug { get; set; } = "";
        public byte CategoryId { get; set; }
        public string TitleTr { get; set; } = "";
        public string TitleEn { get; set; } = "";
        public string? DescriptionTr { get; set; }
        public CefrLevel Level { get; set; } = CefrLevel.A2;
        public short SortOrder { get; set; }
        public List<SeedWord> Words { get; set; } = [];
        public List<SeedLine> Dialogue { get; set; } = [];
    }

    public sealed class SeedWord
    {
        public string Lemma { get; set; } = "";
        public PartOfSpeech Pos { get; set; }
        public CefrLevel Level { get; set; } = CefrLevel.A2;
        public int? Rank { get; set; }
        public string? Ipa { get; set; }
        public string MeaningTr { get; set; } = "";
        public string? DefinitionEn { get; set; }
        public string? NoteTr { get; set; }
        public string ExampleEn { get; set; } = "";
        public string ExampleTr { get; set; } = "";
        /// <summary>Cümlede kelime farklı çekimle geçiyorsa (took, children...) burada belirtilir.</summary>
        public List<string>? Forms { get; set; }
    }

    public sealed class SeedLine
    {
        public string Speaker { get; set; } = "";
        public string En { get; set; } = "";
        public string Tr { get; set; } = "";
    }

    public async Task SeedAsync(CancellationToken ct = default)
    {
        var path = Path.Combine(env.ContentRootPath, "Content", "seed-content.json");
        if (!File.Exists(path))
        {
            logger.LogWarning("Başlangıç içerik dosyası bulunamadı: {Path}", path);
            return;
        }

        var file = JsonSerializer.Deserialize<SeedFile>(await File.ReadAllTextAsync(path, ct), JsonDefaults.Options) ?? new SeedFile();
        await using var c = await db.OpenAsync(ct);
        var existing = (await c.QueryAsync<string>("SELECT Slug FROM content.Scenarios")).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var scenario in file.Scenarios.Where(s => !existing.Contains(s.Slug)))
        {
            try
            {
                await SeedScenarioAsync(c, scenario, ct);
                logger.LogInformation("Senaryo yüklendi: {Slug} ({Words} kelime, {Lines} satır)",
                    scenario.Slug, scenario.Words.Count, scenario.Dialogue.Count);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Senaryo yüklenemedi: {Slug}", scenario.Slug);
            }
        }
    }

    private Task SeedScenarioAsync(SqlConnection c, SeedScenario s, CancellationToken ct) =>
        ImportScenarioAsync(c, s, ContentStatus.Published, null, false, ct);

    /// <summary>
    /// Bir senaryoyu (kelimeler, örnek cümleler, diyalog) tek transaction'da yazar.
    /// AI ile üretilen senaryolar Taslak olarak gelir, admin onaylayınca yayına girer.
    /// </summary>
    public async Task<int> ImportScenarioAsync(SqlConnection c, SeedScenario s, ContentStatus status, int? createdBy, bool isAiGenerated, CancellationToken ct)
    {
        var now = clock.Now;
        var st = (byte)status;
        await using var tx = (SqlTransaction)await c.BeginTransactionAsync(ct);
        try
        {
            var scenarioId = await c.ExecuteScalarAsync<int>(new CommandDefinition(
                """
                INSERT INTO content.Scenarios (CategoryId, Slug, TitleTr, TitleEn, DescriptionTr, Level, SortOrder, Status, IsAiGenerated, CreatedAt, CreatedBy, PublishedAt)
                VALUES (@CategoryId, @Slug, @TitleTr, @TitleEn, @DescriptionTr, @Level, @SortOrder, @st, @isAiGenerated, @now, @createdBy, CASE WHEN @st = 1 THEN @now END);
                SELECT CAST(SCOPE_IDENTITY() AS int);
                """,
                new { s.CategoryId, s.Slug, s.TitleTr, s.TitleEn, s.DescriptionTr, Level = (byte)s.Level, s.SortOrder, st, isAiGenerated, createdBy, now },
                tx, cancellationToken: ct));

            var wordIds = new List<(int Id, SeedWord Word)>();
            short order = 0;
            foreach (var w in s.Words)
            {
                var wordId = await c.ExecuteScalarAsync<int>(new CommandDefinition(
                    """
                    DECLARE @id int = (SELECT Id FROM content.Words WHERE Lemma = @Lemma AND PartOfSpeech = @Pos);
                    IF @id IS NULL
                    BEGIN
                        INSERT INTO content.Words (Lemma, PartOfSpeech, FrequencyRank, Level, Ipa, MeaningTr, DefinitionEn, UsageNoteTr, Status, CreatedAt)
                        VALUES (@Lemma, @Pos, @Rank, @Level, @Ipa, @MeaningTr, @DefinitionEn, @NoteTr, @st, @now);
                        SET @id = CAST(SCOPE_IDENTITY() AS int);
                    END
                    SELECT @id;
                    """,
                    new
                    {
                        w.Lemma, Pos = (byte)w.Pos, w.Rank, Level = (byte)w.Level, w.Ipa, w.MeaningTr,
                        w.DefinitionEn, w.NoteTr, st, now
                    }, tx, cancellationToken: ct));

                await c.ExecuteAsync(new CommandDefinition(
                    """
                    IF NOT EXISTS (SELECT 1 FROM content.ScenarioWords WHERE ScenarioId = @scenarioId AND WordId = @wordId)
                        INSERT INTO content.ScenarioWords (ScenarioId, WordId, SortOrder) VALUES (@scenarioId, @wordId, @order)
                    """, new { scenarioId, wordId, order = order++ }, tx, cancellationToken: ct));

                // Örnek cümle (senaryodan bağımsız)
                if (!string.IsNullOrWhiteSpace(w.ExampleEn))
                {
                    var exampleId = await InsertSentenceAsync(c, tx, null, null, null, w.ExampleEn, w.ExampleTr, w.Level, st, now, ct);
                    await LinkAsync(c, tx, exampleId, wordId, w, w.ExampleEn, ct);
                }

                wordIds.Add((wordId, w));
            }

            short line = 1;
            foreach (var d in s.Dialogue)
            {
                var sentenceId = await InsertSentenceAsync(c, tx, scenarioId, line++, d.Speaker, d.En, d.Tr, s.Level, st, now, ct);
                foreach (var (wordId, w) in wordIds)
                    await LinkAsync(c, tx, sentenceId, wordId, w, d.En, ct);
            }

            await tx.CommitAsync(ct);
            return scenarioId;
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    private static Task<int> InsertSentenceAsync(SqlConnection c, SqlTransaction tx, int? scenarioId, short? lineOrder, string? speaker,
        string en, string tr, CefrLevel level, byte status, DateTime now, CancellationToken ct) =>
        c.ExecuteScalarAsync<int>(new CommandDefinition(
            """
            INSERT INTO content.Sentences (ScenarioId, LineOrder, SpeakerRole, TextEn, TextTr, Level, Status, CreatedAt)
            VALUES (@scenarioId, @lineOrder, @speaker, @en, @tr, @level, @status, @now);
            SELECT CAST(SCOPE_IDENTITY() AS int);
            """, new { scenarioId, lineOrder, speaker, en, tr, level = (byte)level, status, now }, tx, cancellationToken: ct));

    private static async Task LinkAsync(SqlConnection c, SqlTransaction tx, int sentenceId, int wordId, SeedWord w, string text, CancellationToken ct)
    {
        var match = FindWord(text, w.Lemma, w.Forms);
        if (match is null) return;

        await c.ExecuteAsync(new CommandDefinition(
            """
            IF NOT EXISTS (SELECT 1 FROM content.SentenceWords WHERE SentenceId = @sentenceId AND WordId = @wordId)
                INSERT INTO content.SentenceWords (SentenceId, WordId, CharStart, CharLength) VALUES (@sentenceId, @wordId, @start, @length)
            """, new { sentenceId, wordId, start = (short)match.Value.Start, length = (short)match.Value.Length }, tx, cancellationToken: ct));
    }

    /// <summary>Kelimeyi cümlede bulur: önce verilen çekimler, sonra yaygın İngilizce ekler (-s, -ed, -ing...).</summary>
    public static (int Start, int Length)? FindWord(string text, string lemma, IEnumerable<string>? forms = null)
    {
        var candidates = new List<string>();
        if (forms is not null) candidates.AddRange(forms);
        candidates.Add(lemma);

        if (!lemma.Contains(' '))
        {
            candidates.AddRange([lemma + "s", lemma + "es", lemma + "ed", lemma + "d", lemma + "ing"]);
            if (lemma.EndsWith('e')) candidates.Add(lemma[..^1] + "ing");
            if (lemma.EndsWith('y')) candidates.AddRange([lemma[..^1] + "ies", lemma[..^1] + "ied"]);
        }

        foreach (var candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var m = Regex.Match(text, $@"(?<![\p{{L}}']){Regex.Escape(candidate)}(?![\p{{L}}])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (m.Success) return (m.Index, m.Length);
        }
        return null;
    }
}
