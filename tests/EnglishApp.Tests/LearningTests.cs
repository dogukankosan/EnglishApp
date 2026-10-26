using EnglishApp.Api.Data;
using EnglishApp.Api.Services;
using EnglishApp.Api.Services.Ai;
using EnglishApp.Shared.Enums;

namespace EnglishApp.Tests;

public class DictationCheckerTests
{
    [Fact]
    public void Exact_match_scores_100_ignoring_case_and_punctuation()
    {
        var r = DictationChecker.Check("Could I have the bill, please?", "could i have the bill please");
        Assert.Equal(100, r.ScorePercent);
        Assert.All(r.Tokens, t => Assert.Equal("ok", t.Status));
        Assert.Empty(r.ExtraWords);
    }

    [Fact]
    public void Missing_words_are_marked_and_lower_the_score()
    {
        var r = DictationChecker.Check("I am going to the station", "I going station");
        Assert.True(r.ScorePercent < 70);
        Assert.Equal("missed", r.Tokens.Single(t => t.Word == "am").Status);
        Assert.Equal("ok", r.Tokens.Single(t => t.Word == "station").Status);
    }

    [Fact]
    public void Small_typo_in_long_word_counts_as_typo_with_half_credit()
    {
        var r = DictationChecker.Check("The restaurant is closed", "The restaurent is closed");
        var token = r.Tokens.Single(t => t.Word == "restaurant");
        Assert.Equal("typo", token.Status);
        Assert.Equal("restaurent", token.Typed);
        Assert.InRange(r.ScorePercent, 80, 95);
    }

    [Fact]
    public void Extra_words_are_reported()
    {
        var r = DictationChecker.Check("See you later", "See you much later");
        Assert.Contains("much", r.ExtraWords);
    }

    [Fact]
    public void Empty_answer_scores_zero()
    {
        var r = DictationChecker.Check("Nice to meet you", "");
        Assert.Equal(0, r.ScorePercent);
        Assert.All(r.Tokens, t => Assert.Equal("missed", t.Status));
    }

    [Fact]
    public void Connected_speech_tips_are_found()
    {
        var tips = DictationChecker.Tips("I'm going to call you, I want to see what you got to say.");
        Assert.Contains(tips, t => t.Sounds.Contains("gonna"));
        Assert.Contains(tips, t => t.Sounds.Contains("wanna"));
        Assert.True(tips.Count <= 4);
    }

    [Theory]
    [InlineData("kitten", "sitting", 3)]
    [InlineData("same", "same", 0)]
    [InlineData("", "abc", 3)]
    public void Levenshtein_distance(string a, string b, int expected) =>
        Assert.Equal(expected, DictationChecker.Levenshtein(a, b));
}

public class StreakFreezeTests
{
    // 25 Ekim 2026 Pazar; hafta 19-25 Ekim
    private static readonly DateTime Today = new(2026, 10, 25);

    private static Dictionary<DateTime, DailyStatsRow> Days(params int[] daysAgo) =>
        daysAgo.ToDictionary(d => Today.AddDays(-d), d => new DailyStatsRow { StatDate = Today.AddDays(-d), ReviewCount = 5, NewCount = 10 });

    [Fact]
    public void One_missed_day_per_week_does_not_break_streak_when_freeze_on()
    {
        Assert.Equal(4, StatsService.CalculateStreak(Days(0, 1, 3, 4), Today, freeze: true).Streak);
        Assert.Equal(2, StatsService.CalculateStreak(Days(0, 1, 3, 4), Today, freeze: false).Streak);
    }

    [Fact]
    public void Second_missed_day_in_same_week_breaks_streak()
    {
        Assert.Equal(2, StatsService.CalculateStreak(Days(0, 2, 4), Today, freeze: true).Streak);
    }

    [Fact]
    public void Freeze_can_be_used_once_in_each_week()
    {
        // 2 gün önce (bu hafta) değil, 7 gün önce (geçen Pazar) kaçırıldı; 9 gün önce de (geçen hafta) kaçırılmış olsa bozulur
        Assert.Equal(8, StatsService.CalculateStreak(Days(0, 1, 2, 3, 4, 5, 6, 8), Today, freeze: true).Streak);
    }

    [Fact]
    public void Missed_yesterday_and_not_studied_today_keeps_streak_with_freeze()
    {
        var (streak, _, today) = StatsService.CalculateStreak(Days(2, 3), Today, freeze: true);
        Assert.Equal(2, streak);
        Assert.False(today);
    }
}

public class GoalTests
{
    private static readonly DateTime Today = new(2026, 10, 25);

    [Fact]
    public void No_goal_returns_has_goal_false() =>
        Assert.False(StatsService.BuildGoal(null, null, 100, [], Today).HasGoal);

    [Fact]
    public void Ahead_of_plan_with_good_pace()
    {
        var days = Enumerable.Range(0, 14).Select(i => new DailyStatsRow { StatDate = Today.AddDays(-i), NewCount = 10, ReviewCount = 20 });
        var g = StatsService.BuildGoal((byte)CefrLevel.B1, Today.AddDays(100), 500, days, Today);

        Assert.True(g.HasGoal);
        Assert.Equal(1000, g.TargetVocabulary);
        Assert.Equal(500, g.WordsNeeded);
        Assert.Equal(100, g.DaysLeft);
        Assert.Equal(5, g.WordsPerDayNeeded);
        Assert.Equal(8.5, g.CurrentPacePerDay);
        Assert.Equal(100 - 59, g.AheadDays);
        Assert.Equal(50, g.ProgressPercent);
    }

    [Fact]
    public void Behind_plan_when_no_recent_study()
    {
        var g = StatsService.BuildGoal((byte)CefrLevel.B2, Today.AddDays(30), 500, [], Today);
        Assert.True(g.AheadDays < 0);
        Assert.Contains("yeni kelime", g.StatusTr);
    }

    [Fact]
    public void Reached_goal()
    {
        var g = StatsService.BuildGoal((byte)CefrLevel.A2, Today.AddDays(30), 800, [], Today);
        Assert.Equal(0, g.WordsNeeded);
        Assert.Equal(100, g.ProgressPercent);
        Assert.Contains("Tebrikler", g.StatusTr);
    }
}

public class VideoParserTests
{
    [Theory]
    [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ", "dQw4w9WgXcQ")]
    [InlineData("https://youtube.com/watch?feature=share&v=dQw4w9WgXcQ&t=10", "dQw4w9WgXcQ")]
    [InlineData("https://youtu.be/dQw4w9WgXcQ?si=abc", "dQw4w9WgXcQ")]
    [InlineData("https://www.youtube.com/shorts/dQw4w9WgXcQ", "dQw4w9WgXcQ")]
    [InlineData("https://www.youtube.com/embed/dQw4w9WgXcQ", "dQw4w9WgXcQ")]
    [InlineData("dQw4w9WgXcQ", "dQw4w9WgXcQ")]
    [InlineData("https://vimeo.com/123", null)]
    [InlineData("", null)]
    public void Parses_youtube_ids(string url, string? expected) =>
        Assert.Equal(expected, VideoService.ParseYoutubeId(url));

    [Fact]
    public void Parses_youtube_transcript_panel_format()
    {
        var s = VideoService.ParseTranscript("0:00\nHey, how's it going?\n0:03\nPretty good,\nthanks.\n1:02:05\nBye!");
        Assert.Equal(3, s.Count);
        Assert.Equal(0, s[0].Start);
        Assert.Equal("Pretty good, thanks.", s[1].Text);
        Assert.Equal(3725, s[2].Start);
    }

    [Fact]
    public void Parses_inline_and_bracket_timestamps()
    {
        var s = VideoService.ParseTranscript("[00:05] Hello there\n00:09 General Kenobi");
        Assert.Equal(2, s.Count);
        Assert.Equal(5, s[0].Start);
        Assert.Equal("General Kenobi", s[1].Text);
    }

    [Fact]
    public void Parses_srt_and_strips_tags()
    {
        var srt = "1\n00:00:01,500 --> 00:00:03,000\n<i>Where are you going?</i>\n\n2\n00:00:04,000 --> 00:00:06,000\nTo the shop &amp; back.";
        var s = VideoService.ParseTranscript(srt);
        Assert.Equal(2, s.Count);
        Assert.Equal(1.5, s[0].Start);
        Assert.Equal("Where are you going?", s[0].Text);
        Assert.Equal("To the shop & back.", s[1].Text);
    }

    [Fact]
    public void Text_without_timestamps_gives_no_segments() =>
        Assert.Empty(VideoService.ParseTranscript("just some text\nwithout times"));
}

public class ReadingTests
{
    [Theory]
    [InlineData("cities", "city")]
    [InlineData("stopped", "stop")]
    [InlineData("making", "make")]
    [InlineData("running", "run")]
    [InlineData("books", "book")]
    [InlineData("quickly", "quick")]
    public void Candidate_lemmas_include_dictionary_form(string word, string lemma) =>
        Assert.Contains(lemma, ReadingService.CandidateLemmas(word));

    [Theory]
    [InlineData(0, CefrLevel.A1)]
    [InlineData(600, CefrLevel.A2)]
    [InlineData(1200, CefrLevel.B1)]
    [InlineData(3000, CefrLevel.B2)]
    public void Level_from_vocabulary(int vocab, CefrLevel expected) =>
        Assert.Equal(expected, ReadingService.LevelFromVocabulary(vocab));

    [Fact]
    public void Coach_week_starts_on_monday()
    {
        Assert.Equal(new DateTime(2026, 10, 19), CoachService.WeekStartOf(new DateTime(2026, 10, 25)));
        Assert.Equal(new DateTime(2026, 10, 26), CoachService.WeekStartOf(new DateTime(2026, 10, 26, 15, 0, 0)));
    }
}
