using EnglishApp.Shared.Common;

namespace EnglishApp.Tests;

public class RequestLogLevelTests
{
    [Theory]
    [InlineData("/api/v1/ai/tts", true)]
    [InlineData("/bff/ai/tts", true)]
    [InlineData("/api/v1/ai/conversations/14/messages", true)]
    [InlineData("/api/v1/reading/lookup", true)]
    [InlineData("/api/v1/coach/weekly", true)]
    [InlineData("/api/v1/admin/content/words/enrich", true)]
    [InlineData("/api/v1/jobs/run", true)]
    [InlineData("/api/v1/stats/dashboard", false)]
    [InlineData("/api/v1/admin/users", false)]
    [InlineData("/Profile", false)]
    [InlineData(null, false)]
    public void Ai_paths_are_recognised(string? path, bool expected) =>
        Assert.Equal(expected, RequestLogLevels.IsAiPath(path));

    [Fact]
    public void Ai_quota_503_is_warning_but_other_503_is_error()
    {
        Assert.Equal(RequestLogLevel.Warning, RequestLogLevels.Get("/bff/ai/tts", 503, 200, false, 2000));
        Assert.Equal(RequestLogLevel.Error, RequestLogLevels.Get("/api/v1/stats/dashboard", 503, 200, false, 1000));
        Assert.Equal(RequestLogLevel.Error, RequestLogLevels.Get("/api/v1/ai/tts", 500, 200, false, 1000));
    }

    [Fact]
    public void Ai_requests_have_higher_slow_threshold()
    {
        Assert.Equal(RequestLogLevel.Information, RequestLogLevels.Get("/api/v1/ai/conversations/1/messages", 200, 2639, false, 1000));
        Assert.Equal(RequestLogLevel.Warning, RequestLogLevels.Get("/api/v1/ai/conversations/1/messages", 200, 12000, false, 1000));
        Assert.Equal(RequestLogLevel.Warning, RequestLogLevels.Get("/api/v1/stats/dashboard", 200, 1500, false, 1000));
        Assert.Equal(RequestLogLevel.Information, RequestLogLevels.Get("/api/v1/stats/dashboard", 200, 40, false, 1000));
    }

    [Fact]
    public void Exceptions_are_always_errors() =>
        Assert.Equal(RequestLogLevel.Error, RequestLogLevels.Get("/api/v1/ai/tts", 200, 10, true, 1000));
}
