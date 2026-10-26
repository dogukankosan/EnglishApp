using EnglishApp.Shared.Enums;

namespace EnglishApp.Shared.Dtos;

// ---------- Senaryolar ----------

public class ScenarioListItemDto
{
    public int Id { get; set; }
    public string Slug { get; set; } = "";
    public string TitleTr { get; set; } = "";
    public string TitleEn { get; set; } = "";
    public string? DescriptionTr { get; set; }
    public CefrLevel Level { get; set; }
    public byte CategoryId { get; set; }
    public string CategoryName { get; set; } = "";
    public string? IconName { get; set; }
    public int TotalItems { get; set; }
    public int StudiedItems { get; set; }
    public int LearnedItems { get; set; }
    public int ProgressPercent { get; set; }
    public bool IsStarted { get; set; }
}

public class ScenarioDetailDto : ScenarioListItemDto
{
    public List<DialogueLineDto> Lines { get; set; } = [];
    public List<WordDto> Words { get; set; } = [];
}

public class DialogueLineDto
{
    public int SentenceId { get; set; }
    public int? ScenarioId { get; set; }
    /// <summary>Hazır MP3 (varsa). /bff/ önekiyle istenir.</summary>
    public string? AudioUrl { get; set; }
    public string? AudioUrlUk { get; set; }
    public short LineOrder { get; set; }
    public string? SpeakerRole { get; set; }
    public string TextEn { get; set; } = "";
    public string TextTr { get; set; } = "";
}

public class WordDto
{
    public int Id { get; set; }
    public string Lemma { get; set; } = "";
    public PartOfSpeech PartOfSpeech { get; set; }
    public CefrLevel Level { get; set; }
    public string? Ipa { get; set; }
    public string MeaningTr { get; set; } = "";
    public string? DefinitionEn { get; set; }
    public string? UsageNoteTr { get; set; }
    public string? AudioUrl { get; set; }
    public string? AudioUrlUk { get; set; }
}

// ---------- Çalışma ----------

public class StudySessionDto
{
    public int? ScenarioId { get; set; }
    public string? ScenarioTitle { get; set; }
    public List<StudyItemDto> Items { get; set; } = [];
    public int DueCount { get; set; }
    public int NewCount { get; set; }
    public int NewRemainingToday { get; set; }
    public int ReviewRemainingToday { get; set; }
}

public class StudyItemDto
{
    public long? CardId { get; set; }
    public bool IsNew { get; set; }
    public CardType CardType { get; set; }
    public CardState State { get; set; }
    public int? WordId { get; set; }
    public int? SentenceId { get; set; }
    public WordDto? Word { get; set; }
    public ExampleSentenceDto? Example { get; set; }
    public DialogueLineDto? Sentence { get; set; }
    public List<IntervalPreviewDto> Previews { get; set; } = [];
}

public class ExampleSentenceDto
{
    public string TextEn { get; set; } = "";
    public string TextTr { get; set; } = "";
    public int? HighlightStart { get; set; }
    public int? HighlightLength { get; set; }
    public string? AudioUrl { get; set; }
    public string? AudioUrlUk { get; set; }
}

public class IntervalPreviewDto
{
    public Rating Rating { get; set; }
    public string Label { get; set; } = "";
}

public class AnswerRequest
{
    public long? CardId { get; set; }
    public CardType CardType { get; set; }
    public int? WordId { get; set; }
    public int? SentenceId { get; set; }
    public Rating Rating { get; set; }
    public int? DurationMs { get; set; }
}

public class AnswerResultDto
{
    public long CardId { get; set; }
    public CardState State { get; set; }
    public DateTime NextDueAt { get; set; }
    /// <summary>Bir sonraki tekrara kalan süre (saniye). İstemcinin saat diliminden bağımsız.</summary>
    public int DueInSeconds { get; set; }
    public string IntervalLabel { get; set; } = "";
    public bool IsLeech { get; set; }
    public bool BecameLeech { get; set; }
    /// <summary>Kart aynı oturumda tekrar gösterilirse butonların altındaki yeni süreler.</summary>
    public List<IntervalPreviewDto> Previews { get; set; } = [];
}

// ---------- Seviye testi ----------

public class PlacementWordDto
{
    public int WordId { get; set; }
    public string Lemma { get; set; } = "";
    public string MeaningTr { get; set; } = "";
    public CefrLevel Level { get; set; }
    public string? Ipa { get; set; }
}

public class PlacementSubmitRequest
{
    public List<int> KnownWordIds { get; set; } = [];
    public bool Finish { get; set; }
}

// ---------- İstatistik ----------

public class DashboardDto
{
    public string EstimatedLevel { get; set; } = "";
    public string EstimatedLevelNote { get; set; } = "";
    public int VocabularySize { get; set; }
    public PeriodStatsDto Today { get; set; } = new();
    public PeriodStatsDto Week { get; set; } = new();
    public int Streak { get; set; }
    public int BestStreak { get; set; }
    public bool StudiedToday { get; set; }
    public int DueNow { get; set; }
    public int LearningWords { get; set; }
    public int LearnedWords { get; set; }
    public int StrugglingWords { get; set; }
    public int KnownWords { get; set; }
    public bool PlacementTestDone { get; set; }
    public ListeningSummaryDto Listening { get; set; } = new();
    public GoalDto Goal { get; set; } = new();
    public List<DailyStatDto> Last30Days { get; set; } = [];
    public List<HeatmapDayDto> Heatmap { get; set; } = [];
    public List<ScenarioListItemDto> Scenarios { get; set; } = [];
}

public class PeriodStatsDto
{
    public int NewCount { get; set; }
    public int ReviewCount { get; set; }
    public int CorrectCount { get; set; }
    public int AccuracyPercent { get; set; }
    public int StudyMinutes { get; set; }
}

public class DailyStatDto
{
    public DateTime Date { get; set; }
    public int NewCount { get; set; }
    public int ReviewCount { get; set; }
    public int CorrectCount { get; set; }
    public int StudyMinutes { get; set; }
}

public class HeatmapDayDto
{
    public DateTime Date { get; set; }
    public int Count { get; set; }
}

public class WordProgressDto
{
    public int WordId { get; set; }
    public string Lemma { get; set; } = "";
    public string MeaningTr { get; set; } = "";
    public CefrLevel Level { get; set; }
    public string Status { get; set; } = "";
    public int Reps { get; set; }
    public int Lapses { get; set; }
    public DateTime? NextDueAt { get; set; }
    public DateTime? LastReviewAt { get; set; }
    public int? RetentionPercent { get; set; }
    public string? AudioUrl { get; set; }
    public string? AudioUrlUk { get; set; }
}
