using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using EnglishApp.Api.Infrastructure.Llm;
using EnglishApp.Api.Services.Ai;
using EnglishApp.Shared.Dtos;
using EnglishApp.Shared.Enums;
using Microsoft.Extensions.Logging.Abstractions;

namespace EnglishApp.Tests;

public class OpenAiCompatibleClientTests
{
    private sealed class FakeHandler(Func<HttpRequestMessage, string, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<JsonObject> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = await request.Content!.ReadAsStringAsync(ct);
            Bodies.Add(JsonNode.Parse(body)!.AsObject());
            return respond(request, body);
        }
    }

    private sealed class Factory(HttpMessageHandler h) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(h, disposeHandler: false);
    }

    private static HttpResponseMessage Json(string json, HttpStatusCode code = HttpStatusCode.OK) =>
        new(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private const string OkBody =
        """{"choices":[{"message":{"content":"{\"reply\":\"Hi!\"}"}}],"usage":{"prompt_tokens":10,"completion_tokens":5,"total_tokens":20}}""";

    private static readonly LlmProviderConfig Gemini = new(LlmProvider.Gemini, "https://x/", "key", "gemini-flash-latest", "low");
    private static readonly LlmProviderConfig Groq = new(LlmProvider.Groq, "https://y/", "key", "llama", null);

    private static OpenAiCompatibleClient Client(FakeHandler h) => new(new Factory(h), NullLogger<OpenAiCompatibleClient>.Instance);

    [Fact]
    public async Task Gemini_gets_strict_json_schema_and_reasoning_effort()
    {
        var h = new FakeHandler((_, _) => Json(OkBody));
        var r = await Client(h).CompleteAsync(Gemini, [LlmMessage.User("hi")], 0.5, AiPrompts.ChatSchema, default);

        Assert.True(r.Success);
        Assert.Equal(20, r.TotalTokens);
        var body = h.Bodies.Single();
        Assert.Equal("json_schema", body["response_format"]!["type"]!.GetValue<string>());
        Assert.Equal("low", body["reasoning_effort"]!.GetValue<string>());
        Assert.Equal("gemini-flash-latest", body["model"]!.GetValue<string>());
    }

    [Fact]
    public async Task Other_providers_get_json_object_mode()
    {
        var h = new FakeHandler((_, _) => Json(OkBody));
        await Client(h).CompleteAsync(Groq, [LlmMessage.User("hi")], 0.5, AiPrompts.ChatSchema, default);
        Assert.Equal("json_object", h.Bodies.Single()["response_format"]!["type"]!.GetValue<string>());
        Assert.Null(h.Bodies.Single()["reasoning_effort"]);
    }

    [Fact]
    public async Task Retries_without_reasoning_effort_on_400()
    {
        var calls = 0;
        var h = new FakeHandler((_, _) => ++calls == 1 ? Json("{\"error\":\"bad param\"}", HttpStatusCode.BadRequest) : Json(OkBody));
        var r = await Client(h).CompleteAsync(Gemini, [LlmMessage.User("hi")], 0.5, null, default);

        Assert.True(r.Success);
        Assert.Equal(2, h.Bodies.Count);
        Assert.Null(h.Bodies[1]["reasoning_effort"]);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, true)]
    [InlineData(HttpStatusCode.InternalServerError, true)]
    [InlineData(HttpStatusCode.Unauthorized, true)]
    public async Task Quota_and_server_errors_are_retryable(HttpStatusCode code, bool retryable)
    {
        var h = new FakeHandler((_, _) => Json("{}", code));
        var r = await Client(h).CompleteAsync(Groq, [LlmMessage.User("hi")], 0.5, null, default);
        Assert.False(r.Success);
        Assert.Equal(retryable, r.Retryable);
        Assert.Equal($"HTTP_{(int)code}", r.ErrorCode);
    }

    [Fact]
    public async Task Missing_usage_is_estimated()
    {
        var h = new FakeHandler((_, _) => Json("""{"choices":[{"message":{"content":"{\"reply\":\"Hello there\"}"}}]}"""));
        var r = await Client(h).CompleteAsync(Groq, [LlmMessage.User(new string('a', 400))], 0.5, null, default);
        Assert.True(r.TotalTokens > 0);
    }

    [Theory]
    [InlineData("```json\n{\"a\":1}\n```", "{\"a\":1}")]
    [InlineData("Sure! {\"a\":{\"b\":2}} hope it helps", "{\"a\":{\"b\":2}}")]
    [InlineData("{\"a\":1}", "{\"a\":1}")]
    public void ExtractJson_strips_wrappers(string input, string expected) =>
        Assert.Equal(expected, OpenAiCompatibleClient.ExtractJson(input));
}

public class AiPromptTests
{
    [Fact]
    public void Chat_schema_requires_all_fields()
    {
        var required = AiPrompts.ChatSchema.Schema["required"]!.AsArray().Select(n => n!.GetValue<string>()).ToList();
        Assert.Equal(["reply", "replyTr", "feedback", "suggestions", "ended"], required);
    }

    [Theory]
    [InlineData(new[] { "Agent", "Passenger" }, "Agent", "Passenger")]
    [InlineData(new[] { "Passenger", "Driver" }, "Driver", "Passenger")]
    [InlineData(new[] { "Receptionist", "Guest" }, "Receptionist", "Guest")]
    [InlineData(new[] { "Emma", "Can" }, "Emma", "Can")]
    [InlineData(new[] { "Waiter", "Customer" }, "Waiter", "Customer")]
    public void PickRoles_gives_customer_role_to_learner(string[] speakers, string ai, string user)
    {
        var lines = speakers.Select((s, i) => new DialogueLineDto { SpeakerRole = s, TextEn = "x", LineOrder = (short)i }).ToList();
        Assert.Equal((ai, user), ConversationService.PickRoles(lines));
    }

    [Fact]
    public void Writing_tasks_have_unique_codes() =>
        Assert.Equal(WritingService.Tasks.Count, WritingService.Tasks.Select(t => t.Code).Distinct().Count());
}
