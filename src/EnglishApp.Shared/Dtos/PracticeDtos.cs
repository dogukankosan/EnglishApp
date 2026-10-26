using EnglishApp.Shared.Enums;

namespace EnglishApp.Shared.Dtos;

// ---------- Dinleme testi ----------

public class ListeningScenarioDto
{
    public int ScenarioId { get; set; }
    public string TitleTr { get; set; } = "";
    public string TitleEn { get; set; } = "";
    public CefrLevel Level { get; set; }
    public string? IconName { get; set; }
    public int Attempts { get; set; }
    public int? BestPercent { get; set; }
    public int? LastPercent { get; set; }
    public DateTime? LastAt { get; set; }
}

public class ListeningQuizDto
{
    public int ScenarioId { get; set; }
    public string TitleTr { get; set; } = "";
    public string TitleEn { get; set; } = "";
    public CefrLevel Level { get; set; }
    /// <summary>Diyalog (sesleri ile). Metin, cevaplar gönderilene kadar arayüzde gizlenir.</summary>
    public List<DialogueLineDto> Lines { get; set; } = [];
    /// <summary>Sorular (doğru cevap YOK: sunucuda kontrol edilir).</summary>
    public List<QuizQuestionDto> Questions { get; set; } = [];
}

public class QuizQuestionDto
{
    public int Index { get; set; }
    public string QuestionTr { get; set; } = "";
    public List<string> Options { get; set; } = [];
}

public class QuizSubmitRequest
{
    public int ScenarioId { get; set; }
    public List<int> Answers { get; set; } = [];
    public int ReplayCount { get; set; }
    public string? Accent { get; set; }
}

public class QuizResultDto
{
    public int Correct { get; set; }
    public int Total { get; set; }
    public int ScorePercent { get; set; }
    public List<QuizResultItemDto> Items { get; set; } = [];
    public ListeningSummaryDto Summary { get; set; } = new();
}

public class QuizResultItemDto
{
    public string QuestionTr { get; set; } = "";
    public List<string> Options { get; set; } = [];
    public int CorrectIndex { get; set; }
    public int? YourIndex { get; set; }
    public bool IsCorrect { get; set; }
    public string ExplanationTr { get; set; } = "";
}

/// <summary>Aylık dinleme seviyesi (son 30 gün).</summary>
public class ListeningSummaryDto
{
    public int Attempts30d { get; set; }
    public int? AveragePercent30d { get; set; }
    public DateTime? LastAt { get; set; }
    public bool TestDue { get; set; }
}

// ---------- Cümle yakalama ----------

public class CaptureAnalyzeRequest
{
    public string Text { get; set; } = "";
}

public class CaptureAnalysisDto
{
    public bool IsEnglish { get; set; }
    public string Text { get; set; } = "";
    public string? CorrectedText { get; set; }
    public string TranslationTr { get; set; } = "";
    public string ExplanationTr { get; set; } = "";
    public string Level { get; set; } = "A2";
    public List<CaptureItemDto> Items { get; set; } = [];
}

public class CaptureItemDto
{
    public string Lemma { get; set; } = "";
    public string Pos { get; set; } = "Phrase";
    public string MeaningTr { get; set; } = "";
    public string? Ipa { get; set; }
    public string? NoteTr { get; set; }
}

public class CaptureSaveRequest
{
    public string Text { get; set; } = "";
    public string TranslationTr { get; set; } = "";
    public string? SourceNote { get; set; }
    public string Level { get; set; } = "A2";
    public List<CaptureItemDto> Items { get; set; } = [];
}

public class CaptureSaveResultDto
{
    public int SentenceId { get; set; }
    public int NewWordCards { get; set; }
    public bool SentenceCard { get; set; }
}

public class CapturedSentenceDto
{
    public int SentenceId { get; set; }
    public string TextEn { get; set; } = "";
    public string TextTr { get; set; } = "";
    public string? SourceNote { get; set; }
    public DateTime CreatedAt { get; set; }
    public List<string> Words { get; set; } = [];
}

// ---------- Seyahat kiti ----------

public class TravelKitRequest
{
    public string Country { get; set; } = "";
    public string? City { get; set; }
    public string? Purpose { get; set; }
    public int? Days { get; set; }
    public string? Notes { get; set; }
}

public class TravelKitDto
{
    public long Id { get; set; }
    public string Title { get; set; } = "";
    public string Country { get; set; } = "";
    public string? City { get; set; }
    public string? Purpose { get; set; }
    public int? Days { get; set; }
    public bool AddedToCards { get; set; }
    public DateTime CreatedAt { get; set; }
    public TravelKitContentDto Content { get; set; } = new();
}

public class TravelKitContentDto
{
    public string TitleTr { get; set; } = "";
    public string OverviewTr { get; set; } = "";
    public List<string> LocalTipsTr { get; set; } = [];
    public List<TravelSectionDto> Sections { get; set; } = [];
}

public class TravelSectionDto
{
    public string TitleTr { get; set; } = "";
    public string Icon { get; set; } = "chat";
    public List<TravelPhraseDto> Phrases { get; set; } = [];
}

public class TravelPhraseDto
{
    public string En { get; set; } = "";
    public string Tr { get; set; } = "";
    public string? NoteTr { get; set; }
}

public class TravelKitListItemDto
{
    public long Id { get; set; }
    public string Title { get; set; } = "";
    public string Country { get; set; } = "";
    public string? City { get; set; }
    public bool AddedToCards { get; set; }
    public DateTime CreatedAt { get; set; }
}
