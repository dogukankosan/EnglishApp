using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Dapper;
using EnglishApp.Api.Infrastructure.Data;
using EnglishApp.Api.Infrastructure.Errors;
using EnglishApp.Api.Infrastructure.Files;
using EnglishApp.Api.Infrastructure.Llm;
using EnglishApp.Api.Infrastructure.Logging;
using EnglishApp.Api.Infrastructure.Security;
using EnglishApp.Api.Infrastructure.Time;
using EnglishApp.Shared.Common;
using EnglishApp.Shared.Dtos;
using EnglishApp.Shared.Enums;
using FluentValidation;

namespace EnglishApp.Api.Services.Ai;

/// <summary>
/// İçerik yönetimi (admin): AI ile senaryo üretme → taslak → onay; kelime listesi içe aktarma ve
/// eksik anlam/örnekleri AI ile doldurma; Kokoro ses üreticisi için eksik ses listesi.
/// </summary>
public interface IAdminContentService
{
    Task<IReadOnlyList<ScenarioCategoryDto>> CategoriesAsync(CancellationToken ct);
    Task<IReadOnlyList<AdminScenarioDto>> ListAsync(CancellationToken ct);
    Task<AdminScenarioDetailDto> GetAsync(int id, CancellationToken ct);
    Task<int> GenerateAsync(GenerateScenarioRequest req, CancellationToken ct);
    Task SetStatusAsync(int id, ContentStatus status, CancellationToken ct);
    Task DeleteAsync(int id, CancellationToken ct);
    Task<WordPoolStatsDto> WordStatsAsync(CancellationToken ct);
    Task<WordImportResultDto> ImportWordsAsync(WordImportRequest req, CancellationToken ct);
    Task<EnrichResultDto> EnrichWordsAsync(int maxBatches, CancellationToken ct);
    Task<List<AudioManifestItemDto>> AudioManifestAsync(bool onlyMissing, CancellationToken ct);
}

public sealed partial class AdminContentService(
    IDbConnectionFactory db,
    IContentSeeder seeder,
    ILlmService llm,
    IAudioStore audio,
    IAuditService auditLog,
    ICurrentUser currentUser,
    ITrClock clock,
    ILogger<AdminContentService> logger) : IAdminContentService
{
    private const int EnrichBatchSize = 25;

    public async Task<IReadOnlyList<ScenarioCategoryDto>> CategoriesAsync(CancellationToken ct)
    {
        await using var c = await db.OpenAsync(ct);
        return (await c.QueryAsync<ScenarioCategoryDto>(new CommandDefinition(
            "SELECT Id, NameTr FROM content.ScenarioCategories ORDER BY SortOrder", cancellationToken: ct))).ToList();
    }

    public async Task<IReadOnlyList<AdminScenarioDto>> ListAsync(CancellationToken ct)
    {
        await using var c = await db.OpenAsync(ct);
        var rows = (await c.QueryAsync<AdminScenarioDto>(new CommandDefinition(
            """
            SELECT sc.Id, sc.Slug, sc.TitleTr, sc.TitleEn, sc.DescriptionTr, sc.Level, sc.CategoryId, cat.NameTr AS CategoryName,
                   sc.Status, sc.IsAiGenerated, sc.CreatedAt, sc.PublishedAt,
                   (SELECT COUNT(*) FROM content.ScenarioWords sw WHERE sw.ScenarioId = sc.Id) AS WordCount,
                   (SELECT COUNT(*) FROM content.Sentences s WHERE s.ScenarioId = sc.Id) AS LineCount,
                   CAST(CASE WHEN EXISTS (SELECT 1 FROM content.ListeningQuizzes q WHERE q.ScenarioId = sc.Id) THEN 1 ELSE 0 END AS bit) AS HasQuiz
            FROM content.Scenarios sc
            JOIN content.ScenarioCategories cat ON cat.Id = sc.CategoryId
            ORDER BY sc.Status, cat.SortOrder, sc.SortOrder, sc.Id DESC
            """, cancellationToken: ct))).ToList();

        // Eksik hazır ses sayısı (Kokoro üretilmemiş satır/kelime)
        foreach (var r in rows)
        {
            var d = await GetAsync(r.Id, ct);
            r.MissingAudio = d.MissingAudio;
        }
        return rows;
    }

    public async Task<AdminScenarioDetailDto> GetAsync(int id, CancellationToken ct)
    {
        await using var c = await db.OpenAsync(ct);
        using var multi = await c.QueryMultipleAsync(new CommandDefinition(
            """
            SELECT sc.Id, sc.Slug, sc.TitleTr, sc.TitleEn, sc.DescriptionTr, sc.Level, sc.CategoryId, cat.NameTr AS CategoryName,
                   sc.Status, sc.IsAiGenerated, sc.CreatedAt, sc.PublishedAt,
                   CAST(CASE WHEN EXISTS (SELECT 1 FROM content.ListeningQuizzes q WHERE q.ScenarioId = sc.Id) THEN 1 ELSE 0 END AS bit) AS HasQuiz
            FROM content.Scenarios sc JOIN content.ScenarioCategories cat ON cat.Id = sc.CategoryId
            WHERE sc.Id = @id;

            SELECT w.Id, w.Lemma, w.PartOfSpeech, w.Level, w.Ipa, w.MeaningTr, w.DefinitionEn, w.UsageNoteTr
            FROM content.ScenarioWords sw JOIN content.Words w ON w.Id = sw.WordId
            WHERE sw.ScenarioId = @id ORDER BY sw.SortOrder;

            SELECT Id AS SentenceId, ScenarioId, LineOrder, SpeakerRole, TextEn, TextTr
            FROM content.Sentences WHERE ScenarioId = @id ORDER BY LineOrder;
            """, new { id }, cancellationToken: ct));

        var d = await multi.ReadSingleOrDefaultAsync<AdminScenarioDetailDto>() ?? throw AppException.NotFound("Senaryo bulunamadı.");
        d.Words = (await multi.ReadAsync<WordDto>()).ToList();
        d.Lines = (await multi.ReadAsync<DialogueLineDto>()).ToList();
        d.WordCount = d.Words.Count;
        d.LineCount = d.Lines.Count;

        foreach (var w in d.Words) { w.AudioUrl = audio.WordUrl(w.Lemma); w.AudioUrlUk = audio.WordUrl(w.Lemma, uk: true); }
        audio.FillDialogue(d.Lines);
        d.MissingAudio = d.Words.Count(w => w.AudioUrl is null) + d.Lines.Count(l => l.AudioUrl is null);
        return d;
    }

    // ---------- AI ile senaryo üretimi ----------

    public async Task<int> GenerateAsync(GenerateScenarioRequest req, CancellationToken ct)
    {
        string categoryName;
        List<string> existingTitles;
        await using (var c = await db.OpenAsync(ct))
        {
            categoryName = await c.ExecuteScalarAsync<string?>(new CommandDefinition(
                "SELECT NameEn FROM content.ScenarioCategories WHERE Id = @id", new { id = req.CategoryId }, cancellationToken: ct))
                ?? throw new AppException(ErrorCodes.ValidationError, "Kategori bulunamadı.");
            existingTitles = (await c.QueryAsync<string>(new CommandDefinition(
                "SELECT TitleEn FROM content.Scenarios", cancellationToken: ct))).ToList();
        }

        var result = await llm.GenerateJsonAsync<GeneratedScenario>(currentUser.Id, AiFeature.ContentGeneration,
            [LlmMessage.System(ScenarioSystem()), LlmMessage.User(ScenarioUser(req, categoryName, existingTitles))],
            ScenarioSchema, 0.7, IsValid, ct);

        var g = result.Data;
        await using var conn = await db.OpenAsync(ct);
        var slug = await UniqueSlugAsync(conn, g.TitleEn, ct);
        var seed = new ContentSeeder.SeedScenario
        {
            Slug = slug,
            CategoryId = req.CategoryId,
            TitleTr = Cut(g.TitleTr, 150),
            TitleEn = Cut(g.TitleEn, 150),
            DescriptionTr = Cut(g.DescriptionTr, 500),
            Level = req.Level,
            SortOrder = 100,
            Words = g.Words.Take(16).Select(w => new ContentSeeder.SeedWord
            {
                Lemma = Cut(w.Lemma.Trim(), 100),
                Pos = Enum.TryParse<PartOfSpeech>(w.Pos, true, out var p) ? p : (w.Lemma.Contains(' ') ? PartOfSpeech.Phrase : PartOfSpeech.Other),
                Level = Enum.TryParse<CefrLevel>(w.Level, true, out var l) ? l : req.Level,
                Ipa = Blank(w.Ipa),
                MeaningTr = Cut(w.MeaningTr, 300),
                DefinitionEn = Cut(Blank(w.DefinitionEn), 500),
                NoteTr = Cut(Blank(w.NoteTr), 500),
                ExampleEn = Cut(w.ExampleEn, 500),
                ExampleTr = Cut(w.ExampleTr, 500)
            }).ToList(),
            Dialogue = g.Dialogue.Take(20).Select(d => new ContentSeeder.SeedLine
            {
                Speaker = Cut(d.Speaker.Trim(), 50), En = Cut(d.En.Trim(), 500), Tr = Cut(d.Tr.Trim(), 500)
            }).ToList()
        };

        var id = await seeder.ImportScenarioAsync(conn, seed, ContentStatus.Draft, currentUser.Id, true, ct);
        await auditLog.WriteAsync("ScenarioGenerated", "Scenario", id, null, new { slug, req.Topic, Level = req.Level.ToString(), result.Model });
        logger.LogInformation("AI senaryo üretti (taslak): {ScenarioId} {Slug} ({Model})", id, slug, result.Model);
        return id;
    }

    public async Task SetStatusAsync(int id, ContentStatus status, CancellationToken ct)
    {
        var before = await GetAsync(id, ct);
        await db.InTransactionAsync(async (c, tx) =>
        {
            var now = clock.Now;
            await c.ExecuteAsync(new CommandDefinition(
                """
                UPDATE content.Scenarios
                SET Status = @status, UpdatedAt = @now, UpdatedBy = @userId,
                    PublishedAt = CASE WHEN @status = 1 THEN ISNULL(PublishedAt, @now) ELSE PublishedAt END,
                    PublishedBy = CASE WHEN @status = 1 THEN ISNULL(PublishedBy, @userId) ELSE PublishedBy END
                WHERE Id = @id;
                """, new { id, status = (byte)status, now, userId = currentUser.Id }, tx, cancellationToken: ct));

            if (status == ContentStatus.Published)
            {
                // Yayına alınınca senaryonun kelimeleri, diyaloğu ve örnek cümleleri de yayına girer
                await c.ExecuteAsync(new CommandDefinition(
                    """
                    UPDATE content.Sentences SET Status = 1 WHERE ScenarioId = @id AND Status = 0;
                    UPDATE w SET Status = 1 FROM content.Words w JOIN content.ScenarioWords sw ON sw.WordId = w.Id WHERE sw.ScenarioId = @id AND w.Status = 0;
                    UPDATE s SET Status = 1
                    FROM content.Sentences s JOIN content.SentenceWords x ON x.SentenceId = s.Id JOIN content.ScenarioWords sw ON sw.WordId = x.WordId
                    WHERE sw.ScenarioId = @id AND s.ScenarioId IS NULL AND s.OwnerUserId IS NULL AND s.Status = 0;
                    """, new { id }, tx, cancellationToken: ct));
            }
            return 0;
        }, ct);

        await auditLog.WriteAsync("ScenarioStatusChanged", "Scenario", id, new { Status = before.Status.ToString() }, new { Status = status.ToString() });
    }

    public async Task DeleteAsync(int id, CancellationToken ct)
    {
        var s = await GetAsync(id, ct);
        if (s.Status == ContentStatus.Published)
            throw new AppException("PUBLISHED", "Yayındaki senaryo silinemez. Önce arşive al.");

        var deleted = await db.InTransactionAsync(async (c, tx) =>
        {
            var used = await c.ExecuteScalarAsync<bool>(new CommandDefinition(
                """
                SELECT CASE WHEN EXISTS (SELECT 1 FROM study.Cards cd JOIN content.Sentences x ON x.Id = cd.SentenceId WHERE x.ScenarioId = @id)
                              OR EXISTS (SELECT 1 FROM study.QuizAttempts WHERE ScenarioId = @id)
                              OR EXISTS (SELECT 1 FROM study.UserScenarioProgress WHERE ScenarioId = @id)
                              OR EXISTS (SELECT 1 FROM ai.Conversations WHERE ScenarioId = @id) THEN 1 ELSE 0 END
                """, new { id }, tx, cancellationToken: ct));
            if (used) return false;

            await c.ExecuteAsync(new CommandDefinition(
                """
                -- Bu taslak için açılmış, başka yerde kullanılmayan taslak kelimeler de silinir (sahipsiz kalmasın)
                DECLARE @words TABLE (Id int PRIMARY KEY);
                INSERT INTO @words SELECT sw.WordId FROM content.ScenarioWords sw JOIN content.Words w ON w.Id = sw.WordId
                WHERE sw.ScenarioId = @id AND w.Status = 0 AND w.OwnerUserId IS NULL AND w.FrequencyRank IS NULL;
                DECLARE @allWords TABLE (Id int PRIMARY KEY);
                INSERT INTO @allWords SELECT WordId FROM content.ScenarioWords WHERE ScenarioId = @id;
                DELETE FROM content.ScenarioWords WHERE ScenarioId = @id;
                DELETE FROM content.Sentences WHERE ScenarioId = @id;
                DELETE FROM content.Scenarios WHERE Id = @id;
                -- Bu senaryo için eklenmiş taslak örnek cümleler (senaryo dışı, yayınlanmamış, kartı yok)
                DECLARE @ex TABLE (Id int PRIMARY KEY);
                INSERT INTO @ex SELECT DISTINCT s.Id FROM content.Sentences s JOIN content.SentenceWords sw ON sw.SentenceId = s.Id JOIN @allWords x ON x.Id = sw.WordId
                WHERE s.ScenarioId IS NULL AND s.OwnerUserId IS NULL AND s.Status = 0
                  AND NOT EXISTS (SELECT 1 FROM study.Cards cd WHERE cd.SentenceId = s.Id);
                DELETE sw FROM content.SentenceWords sw JOIN @ex e ON e.Id = sw.SentenceId;
                DELETE s FROM content.Sentences s JOIN @ex e ON e.Id = s.Id;
                DELETE w FROM content.Words w JOIN @words x ON x.Id = w.Id
                WHERE NOT EXISTS (SELECT 1 FROM content.ScenarioWords sw WHERE sw.WordId = w.Id)
                  AND NOT EXISTS (SELECT 1 FROM content.SentenceWords sw WHERE sw.WordId = w.Id)
                  AND NOT EXISTS (SELECT 1 FROM study.Cards cd WHERE cd.WordId = w.Id)
                  AND NOT EXISTS (SELECT 1 FROM study.UserKnownWords k WHERE k.WordId = w.Id);
                """, new { id }, tx, cancellationToken: ct));
            return true;
        }, ct);

        if (!deleted) throw new AppException("IN_USE", "Bu senaryo çalışılmış (kart, test veya konuşma var). Silmek yerine arşive al.");
        await auditLog.WriteAsync("ScenarioDeleted", "Scenario", id, new { s.Slug }, null);
    }

    // ---------- Kelime havuzu ----------

    public async Task<WordPoolStatsDto> WordStatsAsync(CancellationToken ct)
    {
        await using var c = await db.OpenAsync(ct);
        var st = await c.QuerySingleAsync<WordPoolStatsDto>(new CommandDefinition(
            """
            SELECT COUNT(*) AS TotalShared,
                   SUM(CASE WHEN FrequencyRank IS NOT NULL AND Status = 1 THEN 1 ELSE 0 END) AS GeneralPool,
                   SUM(CASE WHEN MeaningTr = N'' THEN 1 ELSE 0 END) AS NeedsEnrichment
            FROM content.Words WHERE OwnerUserId IS NULL
            """, cancellationToken: ct));
        st.MissingAudio = (await AudioManifestAsync(true, ct)).Count;
        return st;
    }

    public async Task<WordImportResultDto> ImportWordsAsync(WordImportRequest req, CancellationToken ct)
    {
        var words = req.Text.Split('\n')
            .Select(l => WordLine().Replace(l.Trim(), "").Trim().ToLowerInvariant())
            .Where(w => w.Length is > 0 and <= 60 && w.All(ch => char.IsLetter(ch) || ch is ' ' or '-' or '\''))
            .Distinct().ToList();
        if (words.Count == 0) throw new AppException(ErrorCodes.ValidationError, "Listede geçerli kelime bulunamadı.");

        var result = new WordImportResultDto();
        await db.InTransactionAsync(async (c, tx) =>
        {
            var start = req.StartRank ?? (await c.ExecuteScalarAsync<int?>(new CommandDefinition(
                "SELECT MAX(FrequencyRank) FROM content.Words WHERE OwnerUserId IS NULL", transaction: tx, cancellationToken: ct)) ?? 0) + 1;
            var now = clock.Now;
            for (var i = 0; i < words.Count; i++)
            {
                var rank = start + i;
                var level = rank <= 500 ? 1 : rank <= 1000 ? 2 : rank <= 2000 ? 3 : rank <= 3000 ? 4 : 5;
                // Aynı kelime ortak havuzda (herhangi bir türde) varsa sadece sıklık sırasını işle
                var affected = await c.ExecuteAsync(new CommandDefinition(
                    """
                    IF EXISTS (SELECT 1 FROM content.Words WHERE Lemma = @w AND OwnerUserId IS NULL)
                        UPDATE content.Words SET FrequencyRank = ISNULL(FrequencyRank, @rank) WHERE Lemma = @w AND OwnerUserId IS NULL AND FrequencyRank IS NULL;
                    ELSE
                        INSERT INTO content.Words (Lemma, PartOfSpeech, FrequencyRank, Level, MeaningTr, Status, CreatedAt)
                        VALUES (@w, 12, @rank, @level, N'', 0, @now);
                    """, new { w = words[i], rank, level = (byte)level, now }, tx, cancellationToken: ct));
                if (affected > 0) result.Added++; else result.Skipped++;
            }
            return 0;
        }, ct);

        await auditLog.WriteAsync("WordsImported", "Words", null, null, new { result.Added, result.Skipped });
        return result;
    }

    /// <summary>Anlamı boş kelimeleri 25'erli gruplar halinde AI ile doldurur (Türkçe anlam, telaffuz, örnek cümle).</summary>
    public async Task<EnrichResultDto> EnrichWordsAsync(int maxBatches, CancellationToken ct)
    {
        var res = new EnrichResultDto();
        for (var b = 0; b < Math.Clamp(maxBatches, 1, 10); b++)
        {
            List<(int Id, string Lemma, byte Level)> batch;
            await using (var c = await db.OpenAsync(ct))
            {
                batch = (await c.QueryAsync<(int, string, byte)>(new CommandDefinition(
                    $"SELECT TOP ({EnrichBatchSize}) Id, Lemma, Level FROM content.Words WHERE OwnerUserId IS NULL AND MeaningTr = N'' ORDER BY FrequencyRank, Id",
                    cancellationToken: ct))).ToList();
            }
            if (batch.Count == 0) break;

            var r = await llm.GenerateJsonAsync<EnrichResult>(currentUser.Id, AiFeature.ContentGeneration,
                [LlmMessage.System(EnrichSystem()), LlmMessage.User(string.Join("\n", batch.Select(x => x.Lemma)))],
                EnrichSchema, 0.3, e => e.Words.Count > 0, ct);

            var byLemma = r.Data.Words.Where(w => !string.IsNullOrWhiteSpace(w.MeaningTr))
                .GroupBy(w => w.Lemma.Trim().ToLowerInvariant()).ToDictionary(g => g.Key, g => g.First());

            foreach (var item in batch)
            {
                if (!byLemma.TryGetValue(item.Lemma.ToLowerInvariant(), out var e)) { res.Failed++; continue; }
                await SaveEnrichedAsync(item.Id, item.Lemma, item.Level, e, ct);
                res.Processed++;
            }
        }

        await using (var c = await db.OpenAsync(ct))
            res.Remaining = await c.ExecuteScalarAsync<int>(new CommandDefinition(
                "SELECT COUNT(*) FROM content.Words WHERE OwnerUserId IS NULL AND MeaningTr = N''", cancellationToken: ct));
        logger.LogInformation("Kelime zenginleştirme: {Processed} işlendi, {Failed} atlandı, {Remaining} kaldı", res.Processed, res.Failed, res.Remaining);
        return res;
    }

    private Task SaveEnrichedAsync(int wordId, string lemma, byte level, EnrichWord e, CancellationToken ct) =>
        db.InTransactionAsync(async (c, tx) =>
        {
            var pos = (byte)(Enum.TryParse<PartOfSpeech>(e.Pos, true, out var p) ? p : PartOfSpeech.Other);
            // Aynı lemma + tür zaten varsa (çakışma) bu içe aktarma satırını sil, var olan kalsın
            var dup = await c.ExecuteScalarAsync<bool>(new CommandDefinition(
                "SELECT CASE WHEN EXISTS (SELECT 1 FROM content.Words WHERE Lemma = @lemma AND PartOfSpeech = @pos AND Id <> @wordId) THEN 1 ELSE 0 END",
                new { lemma, pos, wordId }, tx, cancellationToken: ct));
            if (dup)
            {
                await c.ExecuteAsync(new CommandDefinition("DELETE FROM content.Words WHERE Id = @wordId AND MeaningTr = N''", new { wordId }, tx, cancellationToken: ct));
                return 0;
            }

            await c.ExecuteAsync(new CommandDefinition(
                """
                UPDATE content.Words SET PartOfSpeech = @pos, Ipa = @ipa, MeaningTr = @meaning, DefinitionEn = @def, UsageNoteTr = @note,
                       Status = 1, UpdatedAt = @now
                WHERE Id = @wordId
                """,
                new
                {
                    wordId, pos, ipa = Cut(Blank(e.Ipa), 100), meaning = Cut(e.MeaningTr.Trim(), 300), def = Cut(Blank(e.DefinitionEn), 500),
                    note = Cut(Blank(e.NoteTr), 500), now = clock.Now
                }, tx, cancellationToken: ct));

            if (!string.IsNullOrWhiteSpace(e.ExampleEn))
            {
                var sentenceId = await c.ExecuteScalarAsync<int>(new CommandDefinition(
                    """
                    INSERT INTO content.Sentences (ScenarioId, TextEn, TextTr, Level, Status, CreatedAt) VALUES (NULL, @en, @tr, @level, 1, @now);
                    SELECT CAST(SCOPE_IDENTITY() AS int);
                    """, new { en = Cut(e.ExampleEn.Trim(), 500), tr = Cut(e.ExampleTr.Trim(), 500), level, now = clock.Now }, tx, cancellationToken: ct));
                if (ContentSeeder.FindWord(e.ExampleEn, lemma) is { } m)
                    await c.ExecuteAsync(new CommandDefinition(
                        "INSERT INTO content.SentenceWords (SentenceId, WordId, CharStart, CharLength) VALUES (@sentenceId, @wordId, @s, @l)",
                        new { sentenceId, wordId, s = (short)m.Start, l = (short)m.Length }, tx, cancellationToken: ct));
            }
            return 0;
        }, ct);

    // ---------- Kokoro için eksik ses listesi ----------

    public async Task<List<AudioManifestItemDto>> AudioManifestAsync(bool onlyMissing, CancellationToken ct)
    {
        await using var c = await db.OpenAsync(ct);
        var words = (await c.QueryAsync<string>(new CommandDefinition(
            "SELECT Lemma FROM content.Words WHERE OwnerUserId IS NULL AND Status = 1", cancellationToken: ct))).ToList();
        var examples = (await c.QueryAsync<string>(new CommandDefinition(
            """
            SELECT DISTINCT s.TextEn FROM content.Sentences s JOIN content.SentenceWords sw ON sw.SentenceId = s.Id
            WHERE s.ScenarioId IS NULL AND s.OwnerUserId IS NULL AND s.Status = 1
            """, cancellationToken: ct))).ToList();
        var lines = (await c.QueryAsync<(int ScenarioId, string? Speaker, string TextEn)>(new CommandDefinition(
            """
            SELECT s.ScenarioId, s.SpeakerRole, s.TextEn FROM content.Sentences s JOIN content.Scenarios sc ON sc.Id = s.ScenarioId
            WHERE sc.Status IN (0, 1) ORDER BY s.ScenarioId, s.LineOrder
            """, cancellationToken: ct))).ToList();

        var items = new Dictionary<string, AudioManifestItemDto>();
        void Add(string voice, string text, string lang)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            var file = AudioNaming.FileName(voice, text);
            if (onlyMissing && audio.GetPath(file) is not null) return;
            items.TryAdd(file, new AudioManifestItemDto { Voice = voice, Text = text.Trim(), Lang = lang, File = file });
        }

        foreach (var (uk, lang) in new[] { (false, "en-us"), (true, "en-gb") })
        {
            var wordVoice = uk ? AudioNaming.WordVoiceUk : AudioNaming.WordVoice;
            foreach (var w in words) Add(wordVoice, w, lang);
            foreach (var e in examples) Add(wordVoice, e, lang);
            foreach (var g in lines.GroupBy(l => l.ScenarioId))
            {
                var speakers = g.Select(l => l.Speaker).Where(s => s is not null).Cast<string>().Distinct().ToList();
                foreach (var l in g) Add(AudioNaming.VoiceForSpeaker(speakers, l.Speaker, uk), l.TextEn, lang);
            }
        }
        return items.Values.ToList();
    }

    // ---------- Yardımcılar ----------

    private static async Task<string> UniqueSlugAsync(Microsoft.Data.SqlClient.SqlConnection c, string titleEn, CancellationToken ct)
    {
        var baseSlug = SlugChars().Replace(titleEn.ToLowerInvariant(), "-").Trim('-');
        if (baseSlug.Length > 70) baseSlug = baseSlug[..70].Trim('-');
        if (baseSlug.Length == 0) baseSlug = "scenario";
        var slug = baseSlug;
        for (var i = 2; await c.ExecuteScalarAsync<bool>(new CommandDefinition(
                 "SELECT CASE WHEN EXISTS (SELECT 1 FROM content.Scenarios WHERE Slug = @slug) THEN 1 ELSE 0 END", new { slug }, cancellationToken: ct)); i++)
            slug = $"{baseSlug}-{i}";
        return slug;
    }

    [GeneratedRegex(@"[^a-z0-9]+")] private static partial Regex SlugChars();
    [GeneratedRegex(@"^[\d\.\)\s,;\t]+")] private static partial Regex WordLine();

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    private static string Cut(string? s, int max) => s is null ? "" : s.Length <= max ? s : s[..max];

    // ---------- Talimatlar / şemalar ----------

    private static string ScenarioSystem() =>
        "You create real-life English learning scenarios for a language app. The learner is a Turkish native speaker who wants to " +
        "handle situations abroad and especially UNDERSTAND what people say.\n" +
        "Return one scenario:\n" +
        "- titleEn (short, e.g. 'Changing Your Hotel Room'), titleTr (Turkish), descriptionTr (1 sentence, Turkish).\n" +
        "- dialogue: 12-16 lines, exactly 2 speakers with role names (e.g. 'Receptionist' and 'Guest'; the service person speaks first). " +
        "Natural spoken English people really use, at the requested CEFR level (slightly above is fine). Each line: speaker, en, tr (natural Turkish translation).\n" +
        "- words: 10-14 of the most useful words, phrasal verbs and chunks FROM the dialogue. lemma = dictionary form (chunks as natural phrases); " +
        "pos = Noun|Verb|Adjective|Adverb|Phrase|PhrasalVerb|Other; level = CEFR (A1..C1); ipa = US IPA for single words else empty; meaningTr; " +
        "definitionEn = simple English definition; noteTr = short Turkish usage/pronunciation note or empty; " +
        "exampleEn = a NEW short example sentence (not from the dialogue) using the word; exampleTr = its Turkish translation.\n" +
        "Respond ONLY with JSON matching the schema.";

    private static string ScenarioUser(GenerateScenarioRequest r, string category, IEnumerable<string> existing) =>
        $"Category: {category}\nCEFR level: {r.Level}\nTopic/situation: {r.Topic}\nExtra notes: {r.Notes ?? "-"}\n" +
        $"Do NOT repeat these existing scenarios: {string.Join("; ", existing.Take(80))}";

    private static JsonObject S() => new() { ["type"] = "string" };
    private static JsonObject A(JsonObject i) => new() { ["type"] = "array", ["items"] = i };
    private static JsonObject O(params string[] props)
    {
        var p = new JsonObject();
        foreach (var n in props) p[n] = S();
        return new JsonObject { ["type"] = "object", ["properties"] = p, ["required"] = new JsonArray(props.Select(x => (JsonNode)JsonValue.Create(x)!).ToArray()), ["additionalProperties"] = false };
    }
    private static JsonObject Obj(params (string, JsonObject)[] props)
    {
        var p = new JsonObject();
        foreach (var (n, s) in props) p[n] = s;
        return new JsonObject { ["type"] = "object", ["properties"] = p, ["required"] = new JsonArray(props.Select(x => (JsonNode)JsonValue.Create(x.Item1)!).ToArray()), ["additionalProperties"] = false };
    }

    private static readonly LlmJsonSchema ScenarioSchema = new("scenario", Obj(
        ("titleEn", S()), ("titleTr", S()), ("descriptionTr", S()),
        ("dialogue", A(O("speaker", "en", "tr"))),
        ("words", A(O("lemma", "pos", "level", "ipa", "meaningTr", "definitionEn", "noteTr", "exampleEn", "exampleTr")))));

    private sealed class GeneratedScenario
    {
        public string TitleEn { get; set; } = "";
        public string TitleTr { get; set; } = "";
        public string DescriptionTr { get; set; } = "";
        public List<GLine> Dialogue { get; set; } = [];
        public List<GWord> Words { get; set; } = [];
    }
    private sealed class GLine { public string Speaker { get; set; } = ""; public string En { get; set; } = ""; public string Tr { get; set; } = ""; }
    private sealed class GWord
    {
        public string Lemma { get; set; } = ""; public string Pos { get; set; } = ""; public string Level { get; set; } = "";
        public string? Ipa { get; set; } public string MeaningTr { get; set; } = ""; public string? DefinitionEn { get; set; }
        public string? NoteTr { get; set; } public string ExampleEn { get; set; } = ""; public string ExampleTr { get; set; } = "";
    }

    private static bool IsValid(GeneratedScenario g) =>
        !string.IsNullOrWhiteSpace(g.TitleEn) && g.Dialogue.Count >= 8 && g.Words.Count >= 6
        && g.Dialogue.Select(d => d.Speaker).Distinct().Count() >= 2
        && g.Dialogue.All(d => !string.IsNullOrWhiteSpace(d.En) && !string.IsNullOrWhiteSpace(d.Tr))
        && g.Words.All(w => !string.IsNullOrWhiteSpace(w.Lemma) && !string.IsNullOrWhiteSpace(w.MeaningTr));

    private static string EnrichSystem() =>
        "You are building a dictionary for Turkish learners of English. For EACH English word in the user's list return an entry:\n" +
        "lemma (exactly as given), pos = Noun|Verb|Adjective|Adverb|Pronoun|Preposition|Conjunction|Determiner|Interjection|Other (most common use), " +
        "ipa = US IPA, meaningTr = the most common Turkish meaning(s) (short, comma separated), definitionEn = simple English definition, " +
        "noteTr = very short Turkish usage note or empty, exampleEn = a short natural everyday sentence (A2) using the word, exampleTr = its Turkish translation.\n" +
        "Return entries for all words. Respond ONLY with JSON matching the schema.";

    private static readonly LlmJsonSchema EnrichSchema = new("dictionary_batch", Obj(
        ("words", A(O("lemma", "pos", "ipa", "meaningTr", "definitionEn", "noteTr", "exampleEn", "exampleTr")))));

    private sealed class EnrichResult { public List<EnrichWord> Words { get; set; } = []; }
    private sealed class EnrichWord
    {
        public string Lemma { get; set; } = ""; public string Pos { get; set; } = ""; public string? Ipa { get; set; }
        public string MeaningTr { get; set; } = ""; public string? DefinitionEn { get; set; } public string? NoteTr { get; set; }
        public string ExampleEn { get; set; } = ""; public string ExampleTr { get; set; } = "";
    }
}

public sealed class GenerateScenarioRequestValidator : AbstractValidator<GenerateScenarioRequest>
{
    public GenerateScenarioRequestValidator()
    {
        RuleFor(x => x.CategoryId).GreaterThan((byte)0).WithMessage("Kategori seç.");
        RuleFor(x => x.Level).IsInEnum();
        RuleFor(x => x.Topic).NotEmpty().WithMessage("Konu/durum yaz.").MaximumLength(200);
        RuleFor(x => x.Notes).MaximumLength(300);
    }
}

public sealed class WordImportRequestValidator : AbstractValidator<WordImportRequest>
{
    public WordImportRequestValidator()
    {
        RuleFor(x => x.Text).NotEmpty().WithMessage("Kelime listesini yapıştır.").MaximumLength(100_000);
        RuleFor(x => x.StartRank).GreaterThan(0).When(x => x.StartRank.HasValue);
    }
}
