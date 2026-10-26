using EnglishApp.Shared.Enums;

namespace EnglishApp.Shared.Dtos;

// ---------- Dikte ----------

public class DictationItemDto
{
    public int SentenceId { get; set; }
    /// <summary>Cevap: arayüz sonuç gelene kadar gizler (hazır ses yoksa tarayıcı sesi için gerekli).</summary>
    public string TextEn { get; set; } = "";
    public int WordCount { get; set; }
    public string? SpeakerRole { get; set; }
    public string? AudioUrl { get; set; }
    public string? AudioUrlUk { get; set; }
    public int? LastScore { get; set; }
    public string Source { get; set; } = "";
}

public class DictationCheckRequest
{
    public int SentenceId { get; set; }
    public string Typed { get; set; } = "";
}

public class DictationResultDto
{
    public int ScorePercent { get; set; }
    public string CorrectText { get; set; } = "";
    public string TranslationTr { get; set; } = "";
    public List<DictationTokenDto> Tokens { get; set; } = [];
    public List<string> ExtraWords { get; set; } = [];
    public List<ConnectedSpeechTipDto> Tips { get; set; } = [];
}

public class DictationTokenDto
{
    public string Word { get; set; } = "";
    /// <summary>ok | typo | missed</summary>
    public string Status { get; set; } = "ok";
    public string? Typed { get; set; }
}

public class ConnectedSpeechTipDto
{
    public string Written { get; set; } = "";
    public string Sounds { get; set; } = "";
    public string ExplanationTr { get; set; } = "";
}

public class DictationStatsDto
{
    public int Attempts30d { get; set; }
    public int? Average30d { get; set; }
    public int WeakSentences { get; set; }
}

// ---------- Hedef ----------

public class GoalDto
{
    public bool HasGoal { get; set; }
    public string? TargetLevel { get; set; }
    public DateTime? TargetDate { get; set; }
    public int DaysLeft { get; set; }
    public int CurrentVocabulary { get; set; }
    public int TargetVocabulary { get; set; }
    public int WordsNeeded { get; set; }
    public double WordsPerDayNeeded { get; set; }
    public double CurrentPacePerDay { get; set; }
    /// <summary>Pozitif: planın önündesin (gün), negatif: gerisindesin.</summary>
    public int AheadDays { get; set; }
    public string StatusTr { get; set; } = "";
    public int ProgressPercent { get; set; }
}

public class GoalRequest
{
    public string? TargetLevel { get; set; }
    /// <summary>yyyy-MM-dd</summary>
    public string? TargetDate { get; set; }
    public bool StreakFreeze { get; set; } = true;
}

// ---------- Haftalık koç ----------

public class CoachReportDto
{
    public DateTime WeekStart { get; set; }
    public DateTime CreatedAt { get; set; }
    public string SummaryTr { get; set; } = "";
    public List<string> WinsTr { get; set; } = [];
    public List<CoachFocusDto> Focus { get; set; } = [];
    public List<PhraseDto> Phrases { get; set; } = [];
    public List<string> NextWeekPlanTr { get; set; } = [];
}

public class CoachFocusDto
{
    public string TitleTr { get; set; } = "";
    public string WhyTr { get; set; } = "";
    public string PracticeTr { get; set; } = "";
}

// ---------- Okuma ----------

public class ReadingRequest
{
    public string? Topic { get; set; }
    public CefrLevel? Level { get; set; }
}

public class ReadingListItemDto
{
    public long Id { get; set; }
    public string Title { get; set; } = "";
    public CefrLevel Level { get; set; }
    public string? Topic { get; set; }
    public int WordCount { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? ReadAt { get; set; }
}

public class ReadingDto : ReadingListItemDto
{
    public ReadingContentDto Content { get; set; } = new();
}

public class ReadingContentDto
{
    public string Title { get; set; } = "";
    public List<ReadingParagraphDto> Paragraphs { get; set; } = [];
    public List<ReadingGlossDto> Glossary { get; set; } = [];
    public List<ReadingQuestionDto> Questions { get; set; } = [];
}

public class ReadingParagraphDto
{
    public string En { get; set; } = "";
    public string Tr { get; set; } = "";
}

public class ReadingGlossDto
{
    public string Word { get; set; } = "";
    public string Lemma { get; set; } = "";
    public string Pos { get; set; } = "";
    public string MeaningTr { get; set; } = "";
}

public class ReadingQuestionDto
{
    public string QuestionTr { get; set; } = "";
    public List<string> Options { get; set; } = [];
    public int CorrectIndex { get; set; }
}

public class WordLookupRequest
{
    public string Word { get; set; } = "";
    public string? Sentence { get; set; }
}

public class WordLookupDto
{
    public string Lemma { get; set; } = "";
    public string Pos { get; set; } = "";
    public string MeaningTr { get; set; } = "";
    public string? Ipa { get; set; }
    public bool FromDictionary { get; set; }
    public bool AlreadyInCards { get; set; }
}

public class AddWordToCardsRequest
{
    public string Lemma { get; set; } = "";
    public string Pos { get; set; } = "Other";
    public string MeaningTr { get; set; } = "";
    public string? Ipa { get; set; }
    public string Sentence { get; set; } = "";
    public string? SentenceTr { get; set; }
    public string? SourceNote { get; set; }
}

// ---------- Öneriler ----------

public class MediaItemDto
{
    public int Id { get; set; }
    /// <summary>1 Dizi, 2 YouTube, 3 Podcast, 4 Film</summary>
    public byte MediaType { get; set; }
    public string Title { get; set; } = "";
    public CefrLevel Level { get; set; }
    public string? Accent { get; set; }
    public string DescriptionTr { get; set; } = "";
    public string? TipTr { get; set; }
    public string Url { get; set; } = "";
    /// <summary>0 yok, 1 Listemde, 2 İzliyorum, 3 Bitirdim</summary>
    public byte MyStatus { get; set; }
}

public class MediaStatusRequest
{
    public byte Status { get; set; }
}

// ---------- YouTube sahneleri ----------

public class VideoCreateRequest
{
    public string Url { get; set; } = "";
    public string? Title { get; set; }
    /// <summary>YouTube'un "Transkripti göster" panelinden kopyalanan metin (zaman damgalı).</summary>
    public string Transcript { get; set; } = "";
}

public class VideoSegmentDto
{
    public double Start { get; set; }
    public string Text { get; set; } = "";
}

public class VideoDto
{
    public long Id { get; set; }
    public string YoutubeId { get; set; } = "";
    public string Title { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public List<VideoSegmentDto> Segments { get; set; } = [];
}

public class VideoListItemDto
{
    public long Id { get; set; }
    public string YoutubeId { get; set; } = "";
    public string Title { get; set; } = "";
    public int SegmentCount { get; set; }
    public DateTime CreatedAt { get; set; }
}
