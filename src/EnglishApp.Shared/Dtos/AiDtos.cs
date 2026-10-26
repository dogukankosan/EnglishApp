using EnglishApp.Shared.Enums;

namespace EnglishApp.Shared.Dtos;

public class ProviderTestResultDto
{
    public string Provider { get; set; } = "";
    public string Model { get; set; } = "";
    public string Kind { get; set; } = "Sohbet"; // Sohbet / Ses
    public bool Success { get; set; }
    public int DurationMs { get; set; }
    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }
    public string? Sample { get; set; }
}

public class AiQuotaDto
{
    public int? Limit { get; set; }
    public int Used { get; set; }
    public int? Remaining { get; set; }
    public bool IsConfigured { get; set; }
}

// ---------- Konuşma (rol yapma) ----------

public class StartConversationRequest
{
    public int? ScenarioId { get; set; }
    public ConversationMode Mode { get; set; } = ConversationMode.RolePlay;
}

public class SendMessageRequest
{
    public string Text { get; set; } = "";
    /// <summary>Mesaj mikrofondan mı geldi (sadece istatistik/log için).</summary>
    public bool FromVoice { get; set; }
}

public class ConversationListItemDto
{
    public long Id { get; set; }
    public string? Title { get; set; }
    public ConversationMode Mode { get; set; }
    public int? ScenarioId { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime? EndedAt { get; set; }
    public int UserMessageCount { get; set; }
}

public class ConversationDto
{
    public long Id { get; set; }
    public int? ScenarioId { get; set; }
    public string? Title { get; set; }
    public ConversationMode Mode { get; set; }
    public string? AiRole { get; set; }
    public string? UserRole { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime? EndedAt { get; set; }
    public int TotalTokens { get; set; }
    public int MaxUserMessages { get; set; }
    /// <summary>AI rolünün doğal sesi (Gemini TTS ses adı). Aynı rol hep aynı sesle konuşur.</summary>
    public string? TtsVoice { get; set; }
    public List<ChatMessageDto> Messages { get; set; } = [];
    public ConversationSummaryDto? Summary { get; set; }
}

public class ChatMessageDto
{
    public long Id { get; set; }
    public ChatRole Role { get; set; }
    public string Content { get; set; } = "";
    /// <summary>AI mesajının Türkçe çevirisi.</summary>
    public string? Translation { get; set; }
    /// <summary>Kullanıcı mesajı için düzeltme.</summary>
    public MessageFeedbackDto? Feedback { get; set; }
    /// <summary>AI mesajından sonra kullanıcının söyleyebileceği örnek cevaplar.</summary>
    public List<string> Suggestions { get; set; } = [];
    public DateTime CreatedAt { get; set; }
}

public class MessageFeedbackDto
{
    public bool IsCorrect { get; set; }
    public string? Corrected { get; set; }
    public string? ExplanationTr { get; set; }
    public string? BetterWay { get; set; }
}

public class SendMessageResultDto
{
    public ChatMessageDto UserMessage { get; set; } = new();
    public ChatMessageDto Reply { get; set; } = new();
    public bool Ended { get; set; }
    public AiQuotaDto Quota { get; set; } = new();
}

public class ConversationSummaryDto
{
    public int Score { get; set; }
    public string OverallTr { get; set; } = "";
    public List<string> StrengthsTr { get; set; } = [];
    public List<CorrectionDto> Mistakes { get; set; } = [];
    public List<PhraseDto> UsefulPhrases { get; set; } = [];
}

public class CorrectionDto
{
    public string Original { get; set; } = "";
    public string Correction { get; set; } = "";
    public string? Type { get; set; }
    public string ExplanationTr { get; set; } = "";
}

public class PhraseDto
{
    public string En { get; set; } = "";
    public string Tr { get; set; } = "";
}

// ---------- Yazma ----------

public class WritingTaskDto
{
    public string Code { get; set; } = "";
    public string TitleTr { get; set; } = "";
    public string InstructionTr { get; set; } = "";
    public string? HintEn { get; set; }
    public CefrLevel Level { get; set; }
    public int MinWords { get; set; }
    public byte? CategoryId { get; set; }
}

public class WritingSubmitRequest
{
    public string? TaskCode { get; set; }
    /// <summary>Serbest yazıda kullanıcının kendi konusu.</summary>
    public string? PromptText { get; set; }
    public string Text { get; set; } = "";
}

public class WritingResultDto
{
    public long Id { get; set; }
    public string? TaskCode { get; set; }
    public string? PromptText { get; set; }
    public string UserText { get; set; } = "";
    public string CorrectedText { get; set; } = "";
    public int Score { get; set; }
    public string? Level { get; set; }
    public string SummaryTr { get; set; } = "";
    public List<CorrectionDto> Mistakes { get; set; } = [];
    public string BetterVersion { get; set; } = "";
    public List<PhraseDto> NewPhrases { get; set; } = [];
    public DateTime CreatedAt { get; set; }
    public AiQuotaDto? Quota { get; set; }
}

public class WritingListItemDto
{
    public long Id { get; set; }
    public string? PromptText { get; set; }
    public int? Score { get; set; }
    public string? EstimatedLevel { get; set; }
    public string Preview { get; set; } = "";
    public DateTime CreatedAt { get; set; }
}
