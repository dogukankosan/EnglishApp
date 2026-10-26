using System.Security.Cryptography;
using EnglishApp.Api.Data;
using EnglishApp.Api.Infrastructure.Logging;
using EnglishApp.Api.Infrastructure.Security;
using EnglishApp.Api.Infrastructure.Time;
using EnglishApp.Api.Services;

namespace EnglishApp.Tests;

public class EncryptionServiceTests
{
    private readonly EncryptionService _svc = new(EncryptionService.GenerateKey());

    [Fact]
    public void Encrypt_then_decrypt_returns_original_including_turkish()
    {
        const string secret = "Gizli şifre: ĞÜŞİÖÇ ğüşıöç 123!";
        var cipher = _svc.Encrypt(secret);
        Assert.NotEqual(secret, cipher);
        Assert.Equal(secret, _svc.Decrypt(cipher));
    }

    [Fact]
    public void Same_value_encrypts_differently_each_time()
    {
        Assert.NotEqual(_svc.Encrypt("abc"), _svc.Encrypt("abc"));
    }

    [Fact]
    public void Tampered_cipher_is_rejected()
    {
        var bytes = Convert.FromBase64String(_svc.Encrypt("abc"));
        bytes[^1] ^= 0xFF;
        Assert.ThrowsAny<CryptographicException>(() => _svc.Decrypt(Convert.ToBase64String(bytes)));
    }

    [Fact]
    public void Wrong_key_cannot_decrypt()
    {
        var other = new EncryptionService(EncryptionService.GenerateKey());
        Assert.ThrowsAny<CryptographicException>(() => other.Decrypt(_svc.Encrypt("abc")));
    }

    [Fact]
    public void Key_must_be_256_bit() =>
        Assert.Throws<InvalidOperationException>(() => new EncryptionService(Convert.ToBase64String(new byte[16])));
}

public class PasswordServiceTests
{
    private readonly PasswordService _svc = new();

    [Fact]
    public void Hash_verifies_and_rejects_wrong_password()
    {
        var hash = _svc.Hash("Test12345");
        Assert.NotEqual("Test12345", hash);
        Assert.NotEqual(PasswordCheck.Failed, _svc.Verify(hash, "Test12345"));
        Assert.Equal(PasswordCheck.Failed, _svc.Verify(hash, "test12345"));
        Assert.Equal(PasswordCheck.Failed, _svc.Verify("bozuk-hash", "Test12345"));
    }
}

public class TurkishCultureTests
{
    [Fact]
    public void Email_normalization_is_culture_invariant()
    {
        var old = Thread.CurrentThread.CurrentCulture;
        try
        {
            // Türkçe kültürde "I".ToLower() = "ı" olur; NormalizeEmail bundan etkilenmemeli
            Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("tr-TR");
            Assert.Equal("ilker@mail.com", AuthService.NormalizeEmail("  ILKER@MAIL.COM "));
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = old;
        }
    }

    [Fact]
    public void TrClock_returns_utc_plus_3()
    {
        var clock = new TrClock();
        var diff = clock.Now - DateTime.UtcNow;
        Assert.InRange(diff.TotalHours, 2.99, 3.01);
    }

    [Fact]
    public void Sensitive_query_values_are_masked() =>
        Assert.Equal("?a=1&password=***&token=***", LogSanitizer.SafeQuery("?a=1&password=abc&token=xyz"));
}

public class ContentSeederTests
{
    [Theory]
    [InlineData("I booked it online.", "book", "booked")]
    [InlineData("Is breakfast included?", "breakfast", "breakfast")]
    [InlineData("Do you have any luggage to check in?", "check in", "check in")]
    [InlineData("Could I have some water, please?", "Could I have", "Could I have")]
    [InlineData("I'd like a wake-up call at seven.", "wake-up call", "wake-up call")]
    [InlineData("These painkillers are good.", "painkiller", "painkillers")]
    [InlineData("I'm checking in now.", "check in", "checking in")]
    public void FindWord_finds_inflected_forms(string text, string lemma, string expected)
    {
        var forms = lemma == "check in" ? new[] { "checking in" } : null;
        var m = ContentSeeder.FindWord(text, lemma, forms);
        Assert.NotNull(m);
        Assert.Equal(expected, text.Substring(m!.Value.Start, m.Value.Length), ignoreCase: true);
    }

    [Fact]
    public void FindWord_does_not_match_inside_other_words() =>
        Assert.Null(ContentSeeder.FindWord("The gateway is closed.", "gate"));
}

public class StreakTests
{
    private static readonly DateTime Today = new(2026, 10, 25);

    private static Dictionary<DateTime, DailyStatsRow> Days(params int[] daysAgo) =>
        daysAgo.ToDictionary(d => Today.AddDays(-d), d => new DailyStatsRow { StatDate = Today.AddDays(-d), ReviewCount = 5 });

    [Fact]
    public void Streak_counts_consecutive_days_including_today()
    {
        var (streak, best, today) = StatsService.CalculateStreak(Days(0, 1, 2, 5, 6), Today);
        Assert.Equal(3, streak);
        Assert.Equal(3, best);
        Assert.True(today);
    }

    [Fact]
    public void Streak_is_not_broken_until_today_ends()
    {
        var (streak, _, today) = StatsService.CalculateStreak(Days(1, 2, 3, 4), Today);
        Assert.Equal(4, streak);
        Assert.False(today);
    }

    [Fact]
    public void Missing_yesterday_breaks_the_streak()
    {
        var (streak, best, _) = StatsService.CalculateStreak(Days(2, 3, 4), Today);
        Assert.Equal(0, streak);
        Assert.Equal(3, best);
    }

    [Theory]
    [InlineData(0, "A1")]
    [InlineData(600, "A2")]
    [InlineData(1200, "B1")]
    [InlineData(3000, "B2")]
    public void Level_estimate_uses_vocabulary_size(int vocab, string level) =>
        Assert.Equal(level, StatsService.EstimateLevel(vocab, 3000).Level);
}
