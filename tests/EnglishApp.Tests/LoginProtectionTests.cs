using EnglishApp.Api.Infrastructure.Security;
using Microsoft.Extensions.Caching.Memory;

namespace EnglishApp.Tests;

public class LoginAttemptTrackerTests
{
    private static LoginAttemptTracker Create() => new(new MemoryCache(new MemoryCacheOptions()));

    [Fact]
    public void Captcha_not_required_before_threshold()
    {
        var t = Create();
        t.RecordFailure("1.1.1.1", "a@x.com");
        t.RecordFailure("1.1.1.1", "b@x.com");
        Assert.False(t.IsCaptchaRequired("1.1.1.1", "c@x.com"));
    }

    [Fact]
    public void Three_failures_from_same_ip_require_captcha_for_any_email()
    {
        var t = Create();
        for (var i = 0; i < LoginAttemptTracker.CaptchaThreshold; i++) t.RecordFailure("1.1.1.1", $"u{i}@x.com");
        Assert.True(t.IsCaptchaRequired("1.1.1.1", "new@x.com"));
        Assert.True(t.IsCaptchaRequired("1.1.1.1", null));
        Assert.False(t.IsCaptchaRequired("2.2.2.2", "new@x.com"));
    }

    [Fact]
    public void Three_failures_on_same_email_from_different_ips_require_captcha()
    {
        var t = Create();
        t.RecordFailure("1.1.1.1", "Victim@X.com");
        t.RecordFailure("2.2.2.2", "victim@x.com ");
        t.RecordFailure("3.3.3.3", "VICTIM@x.com");
        Assert.True(t.IsCaptchaRequired("9.9.9.9", "victim@x.com"));
        Assert.False(t.IsCaptchaRequired("9.9.9.9", "other@x.com"));
    }

    [Fact]
    public void Successful_login_resets_counters()
    {
        var t = Create();
        for (var i = 0; i < 3; i++) t.RecordFailure("1.1.1.1", "a@x.com");
        t.Reset("1.1.1.1", "a@x.com");
        Assert.False(t.IsCaptchaRequired("1.1.1.1", "a@x.com"));
    }

    [Fact]
    public void Missing_ip_and_email_are_ignored()
    {
        var t = Create();
        for (var i = 0; i < 5; i++) t.RecordFailure(null, " ");
        Assert.False(t.IsCaptchaRequired(null, null));
    }
}
