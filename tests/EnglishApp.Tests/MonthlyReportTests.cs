using EnglishApp.Api.Services;

namespace EnglishApp.Tests;

public class MonthlyReportTests
{
    [Fact]
    public void Longest_run_counts_consecutive_days_inside_month()
    {
        var from = new DateTime(2026, 9, 1); var to = new DateTime(2026, 9, 30);
        var days = new[] { 1, 2, 3, 5, 6, 7, 8, 30 }.Select(d => new DateTime(2026, 9, d));
        Assert.Equal(4, JobService.LongestRun(days, from, to));
        Assert.Equal(0, JobService.LongestRun([], from, to));
    }

    [Fact]
    public void Monthly_comment_reflects_consistency_and_accuracy()
    {
        Assert.Contains("hiç çalışamadın", JobService.MonthlyComment(0, 30, 0, null));
        Assert.Contains("Muhteşem", JobService.MonthlyComment(27, 30, 900, 90));
        Assert.Contains("yeni kelime sayısını artırabilirsin", JobService.MonthlyComment(27, 30, 900, 90));
        Assert.Contains("aralıklı", JobService.MonthlyComment(8, 30, 80, 60));
        Assert.Contains("tekrarlara ağırlık", JobService.MonthlyComment(8, 30, 80, 60));
        Assert.Contains("20-30 dakikaya", JobService.MonthlyComment(8, 30, 80, 60));
    }
}