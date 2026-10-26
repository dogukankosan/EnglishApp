using System.Text;
using System.Text.Json;
using Dapper;
using EnglishApp.Api.Data;
using EnglishApp.Api.Infrastructure;
using EnglishApp.Api.Infrastructure.Data;
using EnglishApp.Api.Infrastructure.Errors;
using EnglishApp.Api.Infrastructure.Llm;
using EnglishApp.Api.Infrastructure.Security;
using EnglishApp.Api.Infrastructure.Settings;
using EnglishApp.Api.Infrastructure.Time;
using EnglishApp.Shared.Dtos;
using EnglishApp.Shared.Enums;
using FluentValidation;

namespace EnglishApp.Api.Services.Ai;

public interface IConversationService
{
    Task<ConversationDto> StartAsync(StartConversationRequest req, CancellationToken ct);
    Task<SendMessageResultDto> SendAsync(long id, SendMessageRequest req, CancellationToken ct);
    Task<ConversationDto> FinishAsync(long id, CancellationToken ct);
    Task<ConversationDto> GetAsync(long id, CancellationToken ct);
    Task<IReadOnlyList<ConversationListItemDto>> ListAsync(CancellationToken ct);
    Task DeleteAsync(long id, CancellationToken ct);
}

public sealed class ConversationService(
    IDbConnectionFactory db,
    ILlmService llm,
    IContentRepository content,
    ISettingsService settings,
    ICurrentUser currentUser,
    ITrClock clock,
    ILogger<ConversationService> logger) : IConversationService
{
    private const int HistoryMessages = 16;
    private static readonly string[] LearnerRoles = ["Passenger", "Guest", "Customer", "Patient", "Can", "You", "Learner", "Tourist"];

    private sealed class ConversationRow
    {
        public long Id { get; set; }
        public int UserId { get; set; }
        public int? ScenarioId { get; set; }
        public ConversationMode Mode { get; set; }
        public string? Title { get; set; }
        public string? AiRole { get; set; }
        public string? UserRole { get; set; }
        public int TotalTokens { get; set; }
        public DateTime StartedAt { get; set; }
        public DateTime? EndedAt { get; set; }
        public string? SummaryJson { get; set; }
    }

    private sealed class MessageRow
    {
        public long Id { get; set; }
        public ChatRole Role { get; set; }
        public string Content { get; set; } = "";
        public string? FeedbackJson { get; set; }
        public DateTime CreatedAt { get; set; }
    }

    /// <summary>AI mesajının ekstra bilgisi (FeedbackJson kolonunda saklanır).</summary>
    private sealed class AssistantExtra
    {
        public string? Translation { get; set; }
        public List<string> Suggestions { get; set; } = [];
    }

    public async Task<ConversationDto> StartAsync(StartConversationRequest req, CancellationToken ct)
    {
        var userId = currentUser.Id;
        string system, title, aiRole, userRole;

        if (req.Mode == ConversationMode.RolePlay)
        {
            if (req.ScenarioId is null) throw new AppException(ErrorCodes.ValidationError, "Rol yapma için senaryo seçilmeli.");
            var sc = (await content.ListScenariosAsync(userId, req.ScenarioId, ct)).FirstOrDefault()
                     ?? throw AppException.NotFound("Senaryo bulunamadı.");
            var lines = await content.GetDialogueAsync(sc.Id, ct);
            (aiRole, userRole) = PickRoles(lines);
            system = AiPrompts.RolePlaySystem(sc.TitleEn, sc.DescriptionTr, aiRole, userRole, lines);
            title = sc.TitleTr;
        }
        else
        {
            (aiRole, userRole, title) = ("Friend", "You", "Serbest sohbet");
            system = AiPrompts.FreeTalkSystem();
        }

        // Önce AI'ın ilk cümlesi alınır; başarısız olursa boş konuşma kaydı oluşmaz
        var result = await llm.GenerateJsonAsync<AiPrompts.ChatTurn>(userId, AiFeature.RolePlay,
            [LlmMessage.System(system), LlmMessage.User(AiPrompts.StartInstruction)],
            AiPrompts.ChatSchema, 0.8, t => !string.IsNullOrWhiteSpace(t.Reply), ct, preferFast: true);

        var now = clock.Now;
        var id = await db.InTransactionAsync(async (c, tx) =>
        {
            var convId = await c.ExecuteScalarAsync<long>(new CommandDefinition(
                """
                INSERT INTO ai.Conversations (UserId, ScenarioId, Mode, Title, AiRole, UserRole, TotalTokens, StartedAt)
                VALUES (@userId, @ScenarioId, @mode, @title, @aiRole, @userRole, @tokens, @now);
                SELECT CAST(SCOPE_IDENTITY() AS bigint);
                """,
                new
                {
                    userId, req.ScenarioId, mode = (byte)req.Mode, title, aiRole, userRole,
                    tokens = result.TotalTokens, now
                }, tx, cancellationToken: ct));

            await InsertMessageAsync(c, tx, convId, ChatRole.Assistant, result.Data.Reply,
                Serialize(new AssistantExtra { Translation = result.Data.ReplyTr, Suggestions = Clean(result.Data.Suggestions) }),
                null, result.TotalTokens, now, ct);
            return convId;
        }, ct);

        logger.LogInformation("AI konuşması başladı. Id: {ConversationId}, Mod: {Mode}, Senaryo: {ScenarioId}", id, req.Mode, req.ScenarioId);
        return await GetAsync(id, ct);
    }

    public async Task<SendMessageResultDto> SendAsync(long id, SendMessageRequest req, CancellationToken ct)
    {
        var userId = currentUser.Id;
        var conv = await GetOwnedAsync(id, ct);
        if (conv.EndedAt is not null) throw new AppException("CONVERSATION_ENDED", "Bu konuşma bitti. Yeni bir konuşma başlat.");

        var history = await GetMessagesAsync(id, ct);
        var maxUser = await settings.GetIntAsync("Llm.MaxMessagesPerConversation", 40, ct);
        if (history.Count(m => m.Role == ChatRole.User) >= maxUser)
            throw new AppException("CONVERSATION_LIMIT", $"Bir konuşmada en fazla {maxUser} mesaj gönderebilirsin. Konuşmayı bitirip değerlendirmeni gör.");

        var text = req.Text.Trim();
        var messages = new List<LlmMessage> { LlmMessage.System(await BuildSystemAsync(conv, ct)), LlmMessage.User(AiPrompts.StartInstruction) };
        foreach (var m in history.TakeLast(HistoryMessages))
            messages.Add(m.Role == ChatRole.User ? LlmMessage.User(m.Content) : LlmMessage.Assistant(m.Content));
        messages.Add(LlmMessage.User(text));

        var result = await llm.GenerateJsonAsync<AiPrompts.ChatTurn>(userId, AiFeature.RolePlay, messages,
            AiPrompts.ChatSchema, 0.8, t => !string.IsNullOrWhiteSpace(t.Reply), ct, preferFast: true);

        var feedback = NormalizeFeedback(result.Data.Feedback);
        var extra = new AssistantExtra { Translation = result.Data.ReplyTr, Suggestions = Clean(result.Data.Suggestions) };
        var now = clock.Now;

        var (userMsgId, replyId) = await db.InTransactionAsync(async (c, tx) =>
        {
            var u = await InsertMessageAsync(c, tx, id, ChatRole.User, text, Serialize(feedback), null, null, now, ct);
            var a = await InsertMessageAsync(c, tx, id, ChatRole.Assistant, result.Data.Reply, Serialize(extra), null, result.TotalTokens, now, ct);
            await c.ExecuteAsync(new CommandDefinition(
                "UPDATE ai.Conversations SET TotalTokens = TotalTokens + @tokens WHERE Id = @id",
                new { id, tokens = result.TotalTokens }, tx, cancellationToken: ct));
            return (u, a);
        }, ct);

        return new SendMessageResultDto
        {
            UserMessage = new ChatMessageDto { Id = userMsgId, Role = ChatRole.User, Content = text, Feedback = feedback, CreatedAt = now },
            Reply = new ChatMessageDto
            {
                Id = replyId, Role = ChatRole.Assistant, Content = result.Data.Reply,
                Translation = extra.Translation, Suggestions = extra.Suggestions, CreatedAt = now
            },
            Ended = result.Data.Ended,
            Quota = await llm.GetQuotaAsync(userId, ct)
        };
    }

    public async Task<ConversationDto> FinishAsync(long id, CancellationToken ct)
    {
        var userId = currentUser.Id;
        var conv = await GetOwnedAsync(id, ct);
        if (conv.EndedAt is not null) return await GetAsync(id, ct);

        var history = await GetMessagesAsync(id, ct);
        string? summaryJson = null;
        var tokens = 0;

        // En az 2 mesaj yazdıysa değerlendirme yapılır (boş konuşmaya token harcanmaz)
        if (history.Count(m => m.Role == ChatRole.User) >= 2)
        {
            var transcript = new StringBuilder();
            foreach (var m in history)
                transcript.AppendLine($"{(m.Role == ChatRole.User ? "USER" : "AI")}: {m.Content}");

            var result = await llm.GenerateJsonAsync<ConversationSummaryDto>(userId, AiFeature.RolePlay,
                [LlmMessage.System(AiPrompts.SummarySystem()), LlmMessage.User(transcript.ToString())],
                AiPrompts.SummarySchema, 0.3, s => !string.IsNullOrWhiteSpace(s.OverallTr), ct);

            result.Data.Score = Math.Clamp(result.Data.Score, 0, 100);
            summaryJson = Serialize(result.Data);
            tokens = result.TotalTokens;
        }

        await using var c = await db.OpenAsync(ct);
        await c.ExecuteAsync(new CommandDefinition(
            """
            UPDATE ai.Conversations SET EndedAt = @now, SummaryJson = @summaryJson, TotalTokens = TotalTokens + @tokens
            WHERE Id = @id AND UserId = @userId
            """, new { id, userId, now = clock.Now, summaryJson, tokens }, cancellationToken: ct));

        logger.LogInformation("AI konuşması bitti. Id: {ConversationId}, Mesaj: {Count}", id, history.Count);
        return await GetAsync(id, ct);
    }

    public async Task<ConversationDto> GetAsync(long id, CancellationToken ct)
    {
        var conv = await GetOwnedAsync(id, ct);
        var messages = await GetMessagesAsync(id, ct);
        return new ConversationDto
        {
            Id = conv.Id,
            ScenarioId = conv.ScenarioId,
            Title = conv.Title,
            Mode = conv.Mode,
            AiRole = conv.AiRole,
            UserRole = conv.UserRole,
            StartedAt = conv.StartedAt,
            EndedAt = conv.EndedAt,
            TotalTokens = conv.TotalTokens,
            MaxUserMessages = await settings.GetIntAsync("Llm.MaxMessagesPerConversation", 40, ct),
            Summary = Deserialize<ConversationSummaryDto>(conv.SummaryJson),
            Messages = messages.Select(ToDto).ToList()
        };
    }

    public async Task<IReadOnlyList<ConversationListItemDto>> ListAsync(CancellationToken ct)
    {
        await using var c = await db.OpenAsync(ct);
        return (await c.QueryAsync<ConversationListItemDto>(new CommandDefinition(
            """
            SELECT TOP (50) cv.Id, cv.Title, cv.Mode, cv.ScenarioId, cv.StartedAt, cv.EndedAt,
                   (SELECT COUNT(*) FROM ai.Messages m WHERE m.ConversationId = cv.Id AND m.Role = 2) AS UserMessageCount
            FROM ai.Conversations cv
            WHERE cv.UserId = @userId
            ORDER BY cv.StartedAt DESC
            """, new { userId = currentUser.Id }, cancellationToken: ct))).ToList();
    }

    public async Task DeleteAsync(long id, CancellationToken ct)
    {
        _ = await GetOwnedAsync(id, ct);
        await using var c = await db.OpenAsync(ct);
        await c.ExecuteAsync(new CommandDefinition(
            "DELETE FROM ai.Conversations WHERE Id = @id AND UserId = @userId", new { id, userId = currentUser.Id }, cancellationToken: ct));
    }

    // ---------- Yardımcılar ----------

    /// <summary>Diyalogdaki iki konuşmacıdan müşteri/yolcu olanı kullanıcıya, hizmet vereni AI'a verir.</summary>
    public static (string AiRole, string UserRole) PickRoles(IReadOnlyList<DialogueLineDto> lines)
    {
        var speakers = lines.Select(l => l.SpeakerRole).Where(s => !string.IsNullOrWhiteSpace(s)).Distinct().Cast<string>().ToList();
        if (speakers.Count == 0) return ("Local person", "Tourist");
        if (speakers.Count == 1) return (speakers[0], "Customer");

        var learner = speakers.FirstOrDefault(s => LearnerRoles.Contains(s, StringComparer.OrdinalIgnoreCase));
        if (learner is not null) return (speakers.First(s => s != learner), learner);
        return (speakers[0], speakers[1]);
    }

    private async Task<string> BuildSystemAsync(ConversationRow conv, CancellationToken ct)
    {
        if (conv.Mode == ConversationMode.FreeTalk || conv.ScenarioId is null) return AiPrompts.FreeTalkSystem();
        var sc = (await content.ListScenariosAsync(conv.UserId, conv.ScenarioId, ct)).FirstOrDefault();
        var lines = await content.GetDialogueAsync(conv.ScenarioId.Value, ct);
        return AiPrompts.RolePlaySystem(sc?.TitleEn ?? conv.Title ?? "Role-play", sc?.DescriptionTr,
            conv.AiRole ?? "Staff", conv.UserRole ?? "Customer", lines);
    }

    private async Task<ConversationRow> GetOwnedAsync(long id, CancellationToken ct)
    {
        await using var c = await db.OpenAsync(ct);
        return await c.QuerySingleOrDefaultAsync<ConversationRow>(new CommandDefinition(
            """
            SELECT Id, UserId, ScenarioId, Mode, Title, AiRole, UserRole, TotalTokens, StartedAt, EndedAt, SummaryJson
            FROM ai.Conversations WHERE Id = @id AND UserId = @userId
            """, new { id, userId = currentUser.Id }, cancellationToken: ct))
            ?? throw AppException.NotFound("Konuşma bulunamadı.");
    }

    private async Task<List<MessageRow>> GetMessagesAsync(long id, CancellationToken ct)
    {
        await using var c = await db.OpenAsync(ct);
        return (await c.QueryAsync<MessageRow>(new CommandDefinition(
            "SELECT Id, Role, Content, FeedbackJson, CreatedAt FROM ai.Messages WHERE ConversationId = @id ORDER BY Id",
            new { id }, cancellationToken: ct))).ToList();
    }

    private static Task<long> InsertMessageAsync(Microsoft.Data.SqlClient.SqlConnection c, Microsoft.Data.SqlClient.SqlTransaction tx,
        long conversationId, ChatRole role, string text, string? feedbackJson, int? inputTokens, int? outputTokens, DateTime now, CancellationToken ct) =>
        c.ExecuteScalarAsync<long>(new CommandDefinition(
            """
            INSERT INTO ai.Messages (ConversationId, Role, Content, FeedbackJson, InputTokens, OutputTokens, CreatedAt)
            VALUES (@conversationId, @role, @text, @feedbackJson, @inputTokens, @outputTokens, @now);
            SELECT CAST(SCOPE_IDENTITY() AS bigint);
            """, new { conversationId, role = (byte)role, text, feedbackJson, inputTokens, outputTokens, now }, tx, cancellationToken: ct));

    private static ChatMessageDto ToDto(MessageRow m)
    {
        var dto = new ChatMessageDto { Id = m.Id, Role = m.Role, Content = m.Content, CreatedAt = m.CreatedAt };
        if (m.Role == ChatRole.User)
        {
            dto.Feedback = Deserialize<MessageFeedbackDto>(m.FeedbackJson);
        }
        else if (Deserialize<AssistantExtra>(m.FeedbackJson) is { } extra)
        {
            dto.Translation = extra.Translation;
            dto.Suggestions = extra.Suggestions;
        }
        return dto;
    }

    private static MessageFeedbackDto NormalizeFeedback(MessageFeedbackDto? f)
    {
        if (f is null) return new MessageFeedbackDto { IsCorrect = true };
        static string? N(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
        return new MessageFeedbackDto
        {
            IsCorrect = f.IsCorrect || N(f.Corrected) is null,
            Corrected = N(f.Corrected),
            ExplanationTr = N(f.ExplanationTr),
            BetterWay = N(f.BetterWay)
        };
    }

    private static List<string> Clean(List<string>? list) =>
        (list ?? []).Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).Take(3).ToList();

    private static string Serialize<T>(T value) => JsonSerializer.Serialize(value, JsonDefaults.Options);

    private static T? Deserialize<T>(string? json) where T : class
    {
        if (string.IsNullOrEmpty(json)) return null;
        try { return JsonSerializer.Deserialize<T>(json, JsonDefaults.Options); }
        catch (JsonException) { return null; }
    }
}

public sealed class StartConversationRequestValidator : AbstractValidator<StartConversationRequest>
{
    public StartConversationRequestValidator()
    {
        RuleFor(x => x.Mode).IsInEnum().WithMessage("Geçersiz konuşma türü.");
        RuleFor(x => x.ScenarioId).NotNull().When(x => x.Mode == ConversationMode.RolePlay).WithMessage("Rol yapma için senaryo seçilmeli.");
    }
}

public sealed class SendMessageRequestValidator : AbstractValidator<SendMessageRequest>
{
    public SendMessageRequestValidator() =>
        RuleFor(x => x.Text).NotEmpty().WithMessage("Mesaj boş olamaz.")
            .MaximumLength(500).WithMessage("Mesaj en fazla 500 karakter olabilir.");
}
