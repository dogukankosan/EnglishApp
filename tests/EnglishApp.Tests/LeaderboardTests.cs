using EnglishApp.Api.Services;
using static EnglishApp.Api.Services.LeaderboardService;

namespace EnglishApp.Tests;

public class LeaderboardTests
{
    // 25 Ekim 2026 Pazar; hafta 19-25 Ekim
    private static readonly DateTime Today = new(2026, 10, 25);

    private static StatRow Day(int userId, int daysAgo, short reviews, short news = 0, int seconds = 600, short correct = -1) => new()
    {
        UserId = userId, StatDate = Today.AddDays(-daysAgo), ReviewCount = reviews, NewCount = news, StudySeconds = seconds,
        CorrectCount = correct < 0 ? reviews : correct
    };

    [Theory]
    [InlineData("today", 2026, 10, 25, "Bugün")]
    [InlineData("week", 2026, 10, 19, "Bu hafta")]
    [InlineData(null, 2026, 10, 19, "Bu hafta")]
    [InlineData("MONTH", 2026, 10, 1, "Bu ay")]
    public void Range_starts_at_correct_day(string? period, int y, int m, int d, string label)
    {
        var (_, from, l) = Range(period, Today);
        Assert.Equal(new DateTime(y, m, d), from);
        Assert.Equal(label, l);
    }

    [Fact]
    public void Invalid_period_is_rejected() =>
        Assert.ThrowsAny<Exception>(() => Range("year", Today));

    [Fact]
    public void Rows_are_ranked_by_points_and_period_is_respected()
    {
        var users = new List<UserRow>
        {
            new() { Id = 1, FullName = "Ali", RoleId = 2, Vocabulary = 300 },
            new() { Id = 2, FullName = "Ayşe", RoleId = 1, Vocabulary = 1200 },
            new() { Id = 3, FullName = "Can", RoleId = 2, Vocabulary = 0 }
        };
        var stats = new List<StatRow>
        {
            Day(1, 0, reviews: 40, news: 10),          // bu hafta: 40 + 20 = 60
            Day(1, 10, reviews: 500, news: 100),       // geçen hafta: sayılmaz
            Day(2, 1, reviews: 50, news: 2, correct: 40), // bu hafta: 50 + 4 = 54, doğruluk %80
        };

        var rows = BuildRows(users, stats, Range("week", Today).From, Today, meId: 2, totalWords: 3000);

        Assert.Equal(["Ali", "Ayşe", "Can"], rows.Select(r => r.FullName));
        Assert.Equal([1, 2, 3], rows.Select(r => r.Rank));
        Assert.Equal(60, rows[0].Points);
        Assert.Equal(54, rows[1].Points);
        Assert.Equal(80, rows[1].AccuracyPercent);
        Assert.Null(rows[2].AccuracyPercent);
        Assert.True(rows[1].IsMe);
        Assert.True(rows[1].IsAdmin);
        Assert.True(rows[0].StudiedToday);
        Assert.False(rows[1].StudiedToday);
        Assert.Equal(1, rows[1].Streak);          // dün çalıştı, bugün henüz değil: seri bozulmaz
        Assert.Equal("B1", rows[1].EstimatedLevel);
        Assert.Equal(Today.AddDays(-1), rows[1].LastStudyDate);
    }

    [Fact]
    public void Equal_points_share_rank()
    {
        var users = new List<UserRow>
        {
            new() { Id = 1, FullName = "Ali" }, new() { Id = 2, FullName = "Bora" }, new() { Id = 3, FullName = "Cem" }
        };
        var stats = new List<StatRow> { Day(1, 0, 10), Day(2, 0, 10) };
        var rows = BuildRows(users, stats, Today, Today, meId: 3, totalWords: 100);
        Assert.Equal([1, 1, 3], rows.Select(r => r.Rank));
        Assert.Equal(0, rows[2].Points);
    }
}
