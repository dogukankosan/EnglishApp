using EnglishApp.Api.Services.Fsrs;
using EnglishApp.Shared.Enums;

namespace EnglishApp.Tests;

public class FsrsSchedulerTests
{
    private static readonly DateTime Now = new(2026, 10, 25, 20, 0, 0);
    private readonly FsrsScheduler _s = new();

    [Fact]
    public void New_card_rated_again_goes_to_first_learning_step()
    {
        var r = _s.Review(FsrsCard.CreateNew(Now), Rating.Again, Now);
        Assert.Equal(CardState.Learning, r.Card.State);
        Assert.Equal(Now.AddMinutes(1), r.Card.Due);
        Assert.Equal(1, r.Card.Reps);
    }

    [Fact]
    public void New_card_rated_good_moves_to_second_step_then_graduates()
    {
        var first = _s.Review(FsrsCard.CreateNew(Now), Rating.Good, Now).Card;
        Assert.Equal(CardState.Learning, first.State);
        Assert.Equal(Now.AddMinutes(10), first.Due);

        var second = _s.Review(first, Rating.Good, first.Due).Card;
        Assert.Equal(CardState.Review, second.State);
        Assert.True(second.Due >= first.Due.AddDays(1));
    }

    [Fact]
    public void New_card_rated_easy_graduates_immediately_with_long_interval()
    {
        var r = _s.Review(FsrsCard.CreateNew(Now), Rating.Easy, Now);
        Assert.Equal(CardState.Review, r.Card.State);
        Assert.True(r.ScheduledDays >= 7, $"Easy aralığı çok kısa: {r.ScheduledDays}");
    }

    [Fact]
    public void Ratings_produce_increasing_initial_stability()
    {
        var s = Enum.GetValues<Rating>().Select(x => _s.Review(FsrsCard.CreateNew(Now), x, Now).Card.Stability).ToList();
        Assert.True(s[0] < s[1] && s[1] < s[2] && s[2] < s[3]);
    }

    [Fact]
    public void Forgetting_a_review_card_increments_lapses_and_goes_to_relearning()
    {
        var card = new FsrsCard(CardState.Review, 10, 5, 0, 5, 0, Now, Now.AddDays(-10));
        var r = _s.Review(card, Rating.Again, Now);
        Assert.Equal(CardState.Relearning, r.Card.State);
        Assert.Equal(1, r.Card.Lapses);
        Assert.True(r.Card.Stability < card.Stability, "Unutunca kalıcılık düşmeli");
        Assert.Equal(Now.AddMinutes(10), r.Card.Due);
    }

    [Fact]
    public void Successful_review_increases_stability_and_interval()
    {
        var card = new FsrsCard(CardState.Review, 10, 5, 0, 5, 0, Now, Now.AddDays(-10));
        var good = _s.Review(card, Rating.Good, Now).Card;
        var easy = _s.Review(card, Rating.Easy, Now).Card;
        var hard = _s.Review(card, Rating.Hard, Now).Card;

        Assert.True(good.Stability > card.Stability);
        Assert.True(easy.Due > good.Due && good.Due > hard.Due);
    }

    [Fact]
    public void Difficulty_stays_within_1_and_10()
    {
        var card = FsrsCard.CreateNew(Now);
        var t = Now;
        for (var i = 0; i < 30; i++)
        {
            card = _s.Review(card, Rating.Again, t).Card;
            t = t.AddMinutes(1);
            Assert.InRange(card.Difficulty, 1, 10);
        }
        for (var i = 0; i < 30; i++)
        {
            card = _s.Review(card, Rating.Easy, card.Due).Card;
            Assert.InRange(card.Difficulty, 1, 10);
        }
    }

    [Fact]
    public void Retrievability_is_90_percent_at_stability_days()
    {
        Assert.Equal(0.9, _s.Retrievability(10, 10), 3);
        Assert.Equal(1.0, _s.Retrievability(0, 10), 3);
    }

    [Fact]
    public void Higher_desired_retention_gives_shorter_intervals()
    {
        var high = new FsrsScheduler(0.95).NextIntervalDays(20);
        var low = new FsrsScheduler(0.85).NextIntervalDays(20);
        Assert.True(high < low);
        Assert.Equal(20, new FsrsScheduler(0.9).NextIntervalDays(20));
    }

    [Theory]
    [InlineData(0.5, "<1 dk")]
    [InlineData(10, "10 dk")]
    [InlineData(90, "2 sa")]
    [InlineData(60 * 24 * 3, "3 gün")]
    [InlineData(60 * 24 * 45, "1,5 ay")]
    public void Interval_labels_are_turkish(double minutes, string expected) =>
        Assert.Equal(expected, FsrsScheduler.FormatInterval(TimeSpan.FromMinutes(minutes)));
}
