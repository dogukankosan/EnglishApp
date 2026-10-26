using System.Text.RegularExpressions;
using Dapper;
using EnglishApp.Api.Data;
using EnglishApp.Api.Infrastructure.Data;
using EnglishApp.Api.Infrastructure.Errors;
using EnglishApp.Api.Infrastructure.Files;
using EnglishApp.Api.Infrastructure.Security;
using EnglishApp.Api.Infrastructure.Time;
using EnglishApp.Shared.Dtos;
using FluentValidation;

namespace EnglishApp.Api.Services;

/// <summary>
/// Dikte (dinle-yaz): cümleyi dinle, duyduğunu yaz; kaçırdığın kelimeler ve birleşen sesler gösterilir.
/// Düşük puan aldığın cümleler bir sonraki turda önce gelir.
/// </summary>
public interface IDictationService
{
    Task<List<DictationItemDto>> NextAsync(int? scenarioId, int count, CancellationToken ct);
    Task<DictationResultDto> CheckAsync(DictationCheckRequest req, CancellationToken ct);
    Task<DictationStatsDto> StatsAsync(CancellationToken ct);
}

public sealed class DictationService(
    IDbConnectionFactory db,
    IContentRepository content,
    IAudioStore audio,
    ICurrentUser currentUser,
    ITrClock clock) : IDictationService
{
    private sealed class PoolRow
    {
        public int Id { get; set; }
        public int? ScenarioId { get; set; }
        public string? SpeakerRole { get; set; }
        public string TextEn { get; set; } = "";
        public int? OwnerUserId { get; set; }
        public byte? LastScore { get; set; }
        public string? ScenarioTitle { get; set; }
        public string? SourceNote { get; set; }
    }

    public async Task<List<DictationItemDto>> NextAsync(int? scenarioId, int count, CancellationToken ct)
    {
        var userId = currentUser.Id;
        count = Math.Clamp(count, 3, 20);
        await using var c = await db.OpenAsync(ct);
        var rows = (await c.QueryAsync<PoolRow>(new CommandDefinition(
            """
            SELECT TOP (@count) s.Id, s.ScenarioId, s.SpeakerRole, s.TextEn, s.OwnerUserId, s.SourceNote, sc.TitleTr AS ScenarioTitle,
                   last.ScorePercent AS LastScore
            FROM content.Sentences s
            LEFT JOIN content.Scenarios sc ON sc.Id = s.ScenarioId
            OUTER APPLY (SELECT TOP 1 a.ScorePercent FROM study.DictationAttempts a
                         WHERE a.UserId = @userId AND a.SentenceId = s.Id ORDER BY a.CreatedAt DESC) last
            WHERE s.Status = 1 AND LEN(s.TextEn) BETWEEN 8 AND 160
              AND (s.OwnerUserId IS NULL OR s.OwnerUserId = @userId)
              AND (s.ScenarioId IS NULL OR sc.Status = 1)
              AND (@scenarioId IS NULL OR s.ScenarioId = @scenarioId)
            ORDER BY CASE WHEN last.ScorePercent < 80 THEN 0 WHEN last.ScorePercent IS NULL THEN 1 ELSE 2 END, NEWID()
            """, new { userId, scenarioId, count }, cancellationToken: ct))).ToList();

        // Diyalog cümlelerinin sesi konuşmacı sırasına göre seçilir
        var speakersByScenario = new Dictionary<int, List<string>>();
        foreach (var sid in rows.Where(r => r.ScenarioId is not null).Select(r => r.ScenarioId!.Value).Distinct())
            speakersByScenario[sid] = (await content.GetDialogueAsync(sid, ct))
                .Select(l => l.SpeakerRole).Where(s => s is not null).Cast<string>().Distinct().ToList();

        return rows.Select(r =>
        {
            var item = new DictationItemDto
            {
                SentenceId = r.Id,
                TextEn = r.TextEn,
                WordCount = r.TextEn.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length,
                SpeakerRole = r.SpeakerRole,
                LastScore = r.LastScore,
                Source = r.ScenarioTitle ?? (r.OwnerUserId is not null ? (r.SourceNote ?? "Kendi cümlem") : "Örnek cümle")
            };
            if (r.ScenarioId is int scId)
            {
                var line = new DialogueLineDto { TextEn = r.TextEn, SpeakerRole = r.SpeakerRole };
                audio.FillLine(line, speakersByScenario[scId]);
                item.AudioUrl = line.AudioUrl; item.AudioUrlUk = line.AudioUrlUk;
            }
            else if (r.OwnerUserId is null)
            {
                item.AudioUrl = audio.WordUrl(r.TextEn); item.AudioUrlUk = audio.WordUrl(r.TextEn, uk: true);
            }
            return item;
        }).ToList();
    }

    public async Task<DictationResultDto> CheckAsync(DictationCheckRequest req, CancellationToken ct)
    {
        var userId = currentUser.Id;
        await using var c = await db.OpenAsync(ct);
        var s = await c.QuerySingleOrDefaultAsync<(string TextEn, string TextTr)>(new CommandDefinition(
            "SELECT TextEn, TextTr FROM content.Sentences WHERE Id = @id AND Status = 1 AND (OwnerUserId IS NULL OR OwnerUserId = @userId)",
            new { id = req.SentenceId, userId }, cancellationToken: ct));
        if (s.TextEn is null) throw AppException.NotFound("Cümle bulunamadı.");

        var result = DictationChecker.Check(s.TextEn, req.Typed ?? "");
        result.TranslationTr = s.TextTr;

        await c.ExecuteAsync(new CommandDefinition(
            "INSERT INTO study.DictationAttempts (UserId, SentenceId, ScorePercent, TypedText, CreatedAt) VALUES (@userId, @sid, @score, @typed, @now)",
            new { userId, sid = req.SentenceId, score = (byte)result.ScorePercent, typed = Cut(req.Typed, 500), now = clock.Now }, cancellationToken: ct));
        return result;
    }

    public async Task<DictationStatsDto> StatsAsync(CancellationToken ct)
    {
        await using var c = await db.OpenAsync(ct);
        return await c.QuerySingleAsync<DictationStatsDto>(new CommandDefinition(
            """
            SELECT COUNT(*) AS Attempts30d, CAST(ROUND(AVG(CAST(ScorePercent AS float)), 0) AS int) AS Average30d,
                   (SELECT COUNT(*) FROM (SELECT SentenceId, ScorePercent, ROW_NUMBER() OVER (PARTITION BY SentenceId ORDER BY CreatedAt DESC) rn
                                          FROM study.DictationAttempts WHERE UserId = @userId) x WHERE x.rn = 1 AND x.ScorePercent < 80) AS WeakSentences
            FROM study.DictationAttempts WHERE UserId = @userId AND CreatedAt >= @from
            """, new { userId = currentUser.Id, from = clock.Today.AddDays(-30) }, cancellationToken: ct));
    }

    private static string? Cut(string? s, int max) => s is null || s.Length <= max ? s : s[..max];
}

/// <summary>Yazılanı hedef cümleyle kelime kelime karşılaştırır (küçük yazım hatalarını "typo" sayar).</summary>
public static partial class DictationChecker
{
    public static DictationResultDto Check(string target, string typed)
    {
        var t = target.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var tn = t.Select(Norm).ToArray();
        var y = typed.Split((char[])[' ', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries);
        var yn = y.Select(Norm).ToArray();

        // En uzun ortak alt dizi (yakın eşleşmeler dahil)
        var dp = new int[tn.Length + 1, yn.Length + 1];
        for (var i = tn.Length - 1; i >= 0; i--)
            for (var j = yn.Length - 1; j >= 0; j--)
                dp[i, j] = Similar(tn[i], yn[j]) ? dp[i + 1, j + 1] + 1 : Math.Max(dp[i + 1, j], dp[i, j + 1]);

        var tokens = t.Select(w => new DictationTokenDto { Word = w, Status = "missed" }).ToList();
        var usedTyped = new bool[yn.Length];
        for (int i = 0, j = 0; i < tn.Length && j < yn.Length;)
        {
            if (Similar(tn[i], yn[j]))
            {
                tokens[i].Status = tn[i] == yn[j] ? "ok" : "typo";
                tokens[i].Typed = tokens[i].Status == "typo" ? y[j] : null;
                usedTyped[j] = true; i++; j++;
            }
            else if (dp[i + 1, j] >= dp[i, j + 1]) i++;
            else j++;
        }
        // Noktalama gibi boş kelimeler doğru sayılır
        for (var i = 0; i < tn.Length; i++) if (tn[i].Length == 0) tokens[i].Status = "ok";

        var scored = tn.Count(w => w.Length > 0);
        var points = tokens.Where((_, i) => tn[i].Length > 0).Sum(x => x.Status == "ok" ? 1.0 : x.Status == "typo" ? 0.5 : 0);
        return new DictationResultDto
        {
            CorrectText = target,
            Tokens = tokens,
            ExtraWords = y.Where((_, j) => !usedTyped[j] && yn[j].Length > 0).ToList(),
            ScorePercent = scored == 0 ? 0 : (int)Math.Round(100 * points / scored),
            Tips = Tips(target)
        };
    }

    public static string Norm(string w) => NonWord().Replace(w.ToLowerInvariant().Replace('’', '\'').Replace('‘', '\''), "");

    private static bool Similar(string a, string b) =>
        a.Length > 0 && b.Length > 0 && (a == b || (a.Length >= 4 && Levenshtein(a, b) <= 1));

    public static int Levenshtein(string a, string b)
    {
        var d = new int[a.Length + 1, b.Length + 1];
        for (var i = 0; i <= a.Length; i++) d[i, 0] = i;
        for (var j = 0; j <= b.Length; j++) d[0, j] = j;
        for (var i = 1; i <= a.Length; i++)
            for (var j = 1; j <= b.Length; j++)
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
        return d[a.Length, b.Length];
    }

    // ---------- Birleşen sesler (connected speech) ----------

    private static readonly (string Pattern, string Written, string Sounds, string Tr)[] Patterns =
    [
        (@"\bgoing to\b(?!\s+(the|a|an|my|your|his|her|our|their|school|work|london|paris|bed)\b)", "going to", "gonna", "Bir şey yapacağını söylerken 'going to' konuşmada 'gonna' gibi duyulur."),
        (@"\bwant to\b", "want to", "wanna", "'want to' hızlı konuşmada 'wanna' olur."),
        (@"\bwants to\b", "wants to", "wansta", "'wants to' kaynaşıp 'wansta' gibi çıkar."),
        (@"\bgot to\b", "got to", "gotta", "'(have) got to' → 'gotta' (yapmalıyım)."),
        (@"\bhave to\b", "have to", "hafta", "'have to' içindeki v sesi f'ye döner: 'hafta'."),
        (@"\bhas to\b", "has to", "hasta", "'has to' → 'hasta' gibi duyulur."),
        (@"\bdid you\b", "did you", "didja", "d + y birleşince 'c/j' sesi çıkar: 'didja'."),
        (@"\bwould you\b", "would you", "wouldja", "'would you' → 'wouldja'."),
        (@"\bcould you\b", "could you", "couldja", "'could you' → 'couldja'. Kibar isteklerde çok duyarsın."),
        (@"\bdon'?t know\b", "don't know", "dunno", "'don't know' gündelik konuşmada 'dunno'."),
        (@"\blet me\b", "let me", "lemme", "'let me' → 'lemme'."),
        (@"\bgive me\b", "give me", "gimme", "'give me' → 'gimme'."),
        (@"\bkind of\b", "kind of", "kinda", "'kind of' → 'kinda' (biraz, sanki)."),
        (@"\bsort of\b", "sort of", "sorta", "'sort of' → 'sorta'."),
        (@"\ba lot of\b", "a lot of", "a lotta", "'a lot of' tek kelime gibi: 'alotta'."),
        (@"\bout of\b", "out of", "outta", "'out of' → 'outta'."),
        (@"\bwhat do you\b", "what do you", "whaddaya", "'what do you' → 'whaddaya'."),
        (@"\bwhat are you\b", "what are you", "whatcha", "'what are you' → 'whatcha'."),
        (@"\bdo you\b", "do you", "d'ya", "'do you' çoğu zaman 'd'ya' kadar kısalır."),
        (@"\bcan you\b", "can you", "kenyu", "'can' vurgusuz olunca 'kın' gibi okunur: 'kın yu'."),
        (@"\bthem\b", "them", "'em", "Vurgusuz 'them' → 'em'. Örn: 'tell 'em'."),
        (@"\bbecause\b", "because", "'cause", "'because' hızlı konuşmada 'cuz'."),
        (@"\b(here you are|there you go|here you go)\b", "here you are / there you go", "hereya / theyago", "Bir şey verirken kalıp tek kelime gibi söylenir."),
        (@"\b\w+[^aeiou\s']\s+(a|an|it|in|on|of|up|out|at|is|and)\b", "ünsüz + ünlü", "kelimeler birleşir", "Ünsüzle biten kelime, ünlüyle başlayan kelimeye bağlanır: 'check in' → 'che-kin', 'pick it up' → 'pi-ki-tup'."),
        (@"\b\w*[aeiou](t|tt)[aeiou]\w*\b", "t (ünlüler arası)", "d / hızlı r", "Amerikan İngilizcesinde ünlüler arasındaki t, d gibi okunur: 'water' → 'wader', 'better' → 'bedder'.")
    ];

    public static List<ConnectedSpeechTipDto> Tips(string sentence)
    {
        var s = sentence.ToLowerInvariant().Replace('’', '\'');
        return Patterns.Where(p => Regex.IsMatch(s, p.Pattern))
            .Select(p => new ConnectedSpeechTipDto { Written = p.Written, Sounds = p.Sounds, ExplanationTr = p.Tr })
            .Take(4).ToList();
    }

    [GeneratedRegex(@"[^a-z0-9']")] private static partial Regex NonWord();
}

public sealed class DictationCheckRequestValidator : AbstractValidator<DictationCheckRequest>
{
    public DictationCheckRequestValidator()
    {
        RuleFor(x => x.SentenceId).GreaterThan(0);
        RuleFor(x => x.Typed).MaximumLength(500);
    }
}
