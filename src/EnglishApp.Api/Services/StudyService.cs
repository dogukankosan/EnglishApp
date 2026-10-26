using EnglishApp.Api.Data;
using EnglishApp.Api.Infrastructure.Errors;
using EnglishApp.Api.Infrastructure.Files;
using EnglishApp.Shared.Common;
using EnglishApp.Api.Infrastructure.Security;
using EnglishApp.Api.Infrastructure.Time;
using EnglishApp.Api.Services.Fsrs;
using EnglishApp.Shared.Dtos;
using EnglishApp.Shared.Enums;
using FluentValidation;

namespace EnglishApp.Api.Services;

public interface IScenarioService
{
    Task<IReadOnlyList<ScenarioListItemDto>> ListAsync(CancellationToken ct);
    Task<ScenarioDetailDto> GetAsync(int id, CancellationToken ct);
}

public sealed class ScenarioService(IContentRepository content, IAudioStore audio, ICurrentUser currentUser) : IScenarioService
{
    public Task<IReadOnlyList<ScenarioListItemDto>> ListAsync(CancellationToken ct) =>
        content.ListScenariosAsync(currentUser.Id, null, ct);

    public async Task<ScenarioDetailDto> GetAsync(int id, CancellationToken ct)
    {
        var item = (await content.ListScenariosAsync(currentUser.Id, id, ct)).FirstOrDefault()
                   ?? throw AppException.NotFound("Senaryo bulunamadı.");

        var detail = new ScenarioDetailDto
        {
            Id = item.Id, Slug = item.Slug, TitleTr = item.TitleTr, TitleEn = item.TitleEn,
            DescriptionTr = item.DescriptionTr, Level = item.Level, CategoryId = item.CategoryId,
            CategoryName = item.CategoryName, IconName = item.IconName, TotalItems = item.TotalItems,
            StudiedItems = item.StudiedItems, LearnedItems = item.LearnedItems,
            ProgressPercent = item.ProgressPercent, IsStarted = item.IsStarted,
            Lines = await content.GetDialogueAsync(id, ct),
            Words = await content.GetScenarioWordsAsync(id, ct)
        };
        audio.FillDialogue(detail.Lines);
        foreach (var w in detail.Words) { w.AudioUrl = audio.WordUrl(w.Lemma); w.AudioUrlUk = audio.WordUrl(w.Lemma, uk: true); }
        return detail;
    }
}

public interface IStudyService
{
    Task<StudySessionDto> GetSessionAsync(int? scenarioId, CancellationToken ct);
    Task<AnswerResultDto> AnswerAsync(AnswerRequest req, CancellationToken ct);
}

public sealed class StudyService(
    IStudyRepository study,
    IUserSettingsRepository settings,
    IContentRepository content,
    IAudioStore audio,
    ICurrentUser currentUser,
    ITrClock clock,
    ILogger<StudyService> logger) : IStudyService
{
    private const int MaxSessionItems = 100;

    public async Task<StudySessionDto> GetSessionAsync(int? scenarioId, CancellationToken ct)
    {
        var userId = currentUser.Id;
        var now = clock.Now;
        var endOfDay = clock.Today.AddDays(1);
        var s = await settings.GetAsync(userId, ct);
        var today = await study.GetDailyStatsAsync(userId, clock.Today, ct);

        string? scenarioTitle = null;
        if (scenarioId is not null)
        {
            var sc = (await content.ListScenariosAsync(userId, scenarioId, ct)).FirstOrDefault()
                     ?? throw AppException.NotFound("Senaryo bulunamadı.");
            scenarioTitle = sc.TitleTr;
            await study.TouchScenarioAsync(userId, scenarioId.Value, now, ct);
        }

        var newDoneToday = today?.NewCount ?? 0;
        var reviewsDoneToday = Math.Max(0, (today?.ReviewCount ?? 0) - newDoneToday);
        var newRemaining = Math.Max(0, s.DailyNewWordLimit - newDoneToday);
        var reviewRemaining = Math.Max(0, s.DailyReviewLimit - reviewsDoneToday);

        // 1) Zamanı gelen tekrarlar  2) Kalan hakla yeni kelimeler  3) Sonra diyalog cümleleri
        var due = await study.GetDueCardsAsync(userId, now, endOfDay, Math.Min(reviewRemaining, MaxSessionItems), scenarioId, ct);
        var newBudget = Math.Min(newRemaining, MaxSessionItems - due.Count);
        var newWordIds = await study.GetNewWordIdsAsync(userId, scenarioId, newBudget, ct);
        // Senaryo kelimeleri bittiyse (karışık çalışmada) genel kelime havuzundan sıklık sırasıyla devam et
        if (scenarioId is null && newWordIds.Count < newBudget)
            newWordIds.AddRange(await study.GetNewGeneralWordIdsAsync(userId, newBudget - newWordIds.Count, newWordIds, ct));
        var newSentenceIds = await study.GetNewSentenceIdsAsync(userId, scenarioId, newBudget - newWordIds.Count, ct);

        var wordIds = due.Where(c => c.WordId is not null).Select(c => c.WordId!.Value).Concat(newWordIds).Distinct().ToList();
        var sentenceIds = due.Where(c => c.SentenceId is not null).Select(c => c.SentenceId!.Value).Concat(newSentenceIds).Distinct().ToList();
        var (words, sentences, examples) = await study.LoadContentAsync(wordIds, sentenceIds, ct, userId);

        // Hazır MP3'ler (Kokoro). Diyalog cümlesinin sesi, senaryodaki konuşmacı sırasına göre seçilir.
        foreach (var w in words) { w.AudioUrl = audio.WordUrl(w.Lemma); w.AudioUrlUk = audio.WordUrl(w.Lemma, uk: true); }
        foreach (var scenarioGroup in sentences.Where(x => x.ScenarioId is not null).GroupBy(x => x.ScenarioId!.Value))
        {
            var speakers = (await content.GetDialogueAsync(scenarioGroup.Key, ct))
                .Select(l => l.SpeakerRole).Where(sp => sp is not null).Cast<string>().Distinct().ToList();
            foreach (var line in scenarioGroup)
                audio.FillLine(line, speakers);
        }

        var wordMap = words.ToDictionary(w => w.Id);
        var sentenceMap = sentences.ToDictionary(x => x.SentenceId);
        var exampleMap = examples.ToDictionary(e => e.WordId);
        var scheduler = CreateScheduler(s.DesiredRetention);

        var items = new List<StudyItemDto>();
        foreach (var card in due)
        {
            var item = BuildItem(card.Id, card.CardType, card.WordId, card.SentenceId, ToFsrs(card), wordMap, sentenceMap, exampleMap, scheduler, now);
            if (item is not null) items.Add(item);
        }

        // Yeni içerikleri tekrarların arasına serpiştir (sıkılmamak ve tekrar ile öğrenmeyi karıştırmak için)
        var newItems = newWordIds
            .Select(id => BuildItem(null, CardType.WordMeaning, id, null, FsrsCard.CreateNew(now), wordMap, sentenceMap, exampleMap, scheduler, now))
            .Concat(newSentenceIds.Select(id => BuildItem(null, CardType.SentenceListening, null, id, FsrsCard.CreateNew(now), wordMap, sentenceMap, exampleMap, scheduler, now)))
            .Where(i => i is not null).Cast<StudyItemDto>().ToList();

        var result = Interleave(items, newItems);
        foreach (var it in result.Where(i => i.Example is not null))
        {
            it.Example!.AudioUrl = audio.WordUrl(it.Example.TextEn);
            it.Example.AudioUrlUk = audio.WordUrl(it.Example.TextEn, uk: true);
        }

        return new StudySessionDto
        {
            ScenarioId = scenarioId,
            ScenarioTitle = scenarioTitle,
            Items = result,
            DueCount = items.Count,
            NewCount = newItems.Count,
            NewRemainingToday = newRemaining,
            ReviewRemainingToday = reviewRemaining
        };
    }

    public async Task<AnswerResultDto> AnswerAsync(AnswerRequest req, CancellationToken ct)
    {
        var userId = currentUser.Id;
        var now = clock.Now;
        var s = await settings.GetAsync(userId, ct);

        CardRow? card = req.CardId is not null
            ? await study.GetCardAsync(userId, req.CardId.Value, ct) ?? throw AppException.NotFound("Kart bulunamadı.")
            : await study.GetCardByTargetAsync(userId, req.CardType, req.WordId, req.SentenceId, ct);

        if (card is null && !await study.ContentExistsAsync(userId, req.WordId, req.SentenceId, ct))
            throw AppException.NotFound("Çalışılacak içerik bulunamadı.");

        var isNew = card is null || card.State == CardState.New;
        var before = card is null ? FsrsCard.CreateNew(now) : ToFsrs(card);
        var scheduler = CreateScheduler(s.DesiredRetention);
        var result = scheduler.Review(before, req.Rating, now);
        var after = result.Card;

        var wasLeech = card?.IsLeech ?? false;
        var isLeech = wasLeech || after.Lapses >= StudyConstants.LeechLapses;

        var cardId = await study.SaveReviewAsync(new ReviewSave(
            userId, card?.Id, card?.CardType ?? req.CardType, card?.WordId ?? req.WordId, card?.SentenceId ?? req.SentenceId,
            before.State, after.State, after.Stability, after.Difficulty, after.Step, after.Reps, after.Lapses,
            after.Due, now, isLeech, req.Rating, result.ElapsedDays, result.ScheduledDays, req.DurationMs,
            clock.Today, isNew), ct);

        if (isLeech && !wasLeech)
            logger.LogInformation("Kart zorlananlar listesine düştü. CardId: {CardId}, Unutma: {Lapses}", cardId, after.Lapses);

        return new AnswerResultDto
        {
            CardId = cardId,
            State = after.State,
            NextDueAt = after.Due,
            DueInSeconds = (int)Math.Min(int.MaxValue, Math.Max(0, (after.Due - now).TotalSeconds)),
            IntervalLabel = FsrsScheduler.FormatInterval(after.Due - now),
            IsLeech = isLeech,
            BecameLeech = isLeech && !wasLeech,
            Previews = scheduler.Preview(after, after.Due)
                .Select(p => new IntervalPreviewDto { Rating = p.Rating, Label = FsrsScheduler.FormatInterval(p.Interval) }).ToList()
        };
    }

    private static StudyItemDto? BuildItem(long? cardId, CardType type, int? wordId, int? sentenceId, FsrsCard fsrs,
        Dictionary<int, WordDto> words, Dictionary<int, DialogueLineDto> sentences, Dictionary<int, ExampleRow> examples,
        FsrsScheduler scheduler, DateTime now)
    {
        var item = new StudyItemDto
        {
            CardId = cardId,
            IsNew = cardId is null || fsrs.State == CardState.New,
            CardType = type,
            State = fsrs.State,
            WordId = wordId,
            SentenceId = sentenceId,
            Previews = scheduler.Preview(fsrs, now)
                .Select(p => new IntervalPreviewDto { Rating = p.Rating, Label = FsrsScheduler.FormatInterval(p.Interval) }).ToList()
        };

        if (wordId is not null)
        {
            if (!words.TryGetValue(wordId.Value, out var w)) return null; // içerik arşivlenmiş
            item.Word = w;
            if (examples.TryGetValue(wordId.Value, out var e))
                item.Example = new ExampleSentenceDto
                {
                    TextEn = e.TextEn, TextTr = e.TextTr, HighlightStart = e.CharStart, HighlightLength = e.CharLength
                };
        }

        if (sentenceId is not null)
        {
            if (!sentences.TryGetValue(sentenceId.Value, out var line)) return null;
            item.Sentence = line;
        }

        return item;
    }

    private static List<StudyItemDto> Interleave(List<StudyItemDto> reviews, List<StudyItemDto> newItems)
    {
        if (reviews.Count == 0) return newItems;
        if (newItems.Count == 0) return reviews;

        var result = new List<StudyItemDto>(reviews.Count + newItems.Count);
        var every = Math.Max(1, reviews.Count / (newItems.Count + 1));
        int r = 0, n = 0;
        while (r < reviews.Count || n < newItems.Count)
        {
            for (var i = 0; i < every && r < reviews.Count; i++) result.Add(reviews[r++]);
            if (n < newItems.Count) result.Add(newItems[n++]);
        }
        return result;
    }

    private static FsrsCard ToFsrs(CardRow c) =>
        new(c.State, c.Stability, c.Difficulty, c.Step, c.Reps, c.Lapses, c.DueAt, c.LastReviewAt);

    private static FsrsScheduler CreateScheduler(decimal desiredRetention) => new((double)desiredRetention);
}

public sealed class AnswerRequestValidator : AbstractValidator<AnswerRequest>
{
    public AnswerRequestValidator()
    {
        RuleFor(x => x.Rating).IsInEnum().WithMessage("Geçersiz puan.");
        RuleFor(x => x.CardType).IsInEnum().WithMessage("Geçersiz kart tipi.");
        RuleFor(x => x).Must(x => x.CardId is not null || x.WordId is not null || x.SentenceId is not null)
            .WithMessage("Kart veya içerik bilgisi gönderilmeli.");
        RuleFor(x => x.WordId).NotNull().When(x => x.CardId is null && x.CardType is CardType.WordMeaning or CardType.WordListening)
            .WithMessage("Kelime kartı için kelime bilgisi gerekli.");
        RuleFor(x => x.SentenceId).NotNull().When(x => x.CardId is null && x.CardType is CardType.SentenceListening or CardType.SentenceCloze)
            .WithMessage("Cümle kartı için cümle bilgisi gerekli.");
        RuleFor(x => x.DurationMs).InclusiveBetween(0, 3_600_000).When(x => x.DurationMs.HasValue);
    }
}
