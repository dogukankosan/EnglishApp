using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Dapper;
using EnglishApp.Api.Data;
using EnglishApp.Api.Infrastructure.Data;
using EnglishApp.Api.Infrastructure.Errors;
using EnglishApp.Api.Infrastructure.Mail;
using EnglishApp.Api.Infrastructure.Settings;
using EnglishApp.Api.Infrastructure.Time;
using EnglishApp.Shared.Enums;

namespace EnglishApp.Api.Services;

public sealed record JobRunResult(int RemindersSent, int RemindersFailed, int WeeklySent, int WeeklyFailed,
    int MonthlySent, int MonthlyFailed, bool CleanupRan, int CleanedRows);

/// <summary>
/// Dış cron servisi (ör. cron-job.org) 15 dakikada bir /api/v1/jobs/run çağırır.
/// IIS uykuya geçse bile hatırlatma ve raporlar böylece zamanında gider.
/// </summary>
public interface IJobService
{
    Task ValidateKeyAsync(string? key, CancellationToken ct);
    Task<JobRunResult> RunAsync(CancellationToken ct);
    /// <summary>Admin: günlük/haftalık/aylık mailin örneğini (kendi verisiyle) kendi adresine gönderir.</summary>
    Task SendSampleReportAsync(int userId, string type, CancellationToken ct);
}

public sealed class JobService(
    IDbConnectionFactory db,
    ISettingsService settings,
    IMailService mail,
    IStatsService statsService,
    IStatsRepository stats,
    IStudyRepository study,
    IRefreshTokenRepository refreshTokens,
    EnglishApp.Api.Services.Ai.ICoachService coach,
    ITrClock clock,
    ILogger<JobService> logger) : IJobService
{
    public sealed class MailTarget
    {
        public int UserId { get; set; }
        public string Email { get; set; } = "";
        public string FullName { get; set; } = "";
    }

    private static readonly SemaphoreSlim RunLock = new(1, 1);
    private static DateTime _lastCleanupDate = DateTime.MinValue;

    public async Task ValidateKeyAsync(string? key, CancellationToken ct)
    {
        var expected = await settings.GetAsync(SettingKeys.JobsSecretKey, ct);
        if (string.IsNullOrEmpty(expected))
            throw new AppException(ErrorCodes.InvalidJobKey, "Jobs.SecretKey ayarlanmamış. Admin > Ayarlar ekranından girin.", StatusCodes.Status403Forbidden);

        var ok = !string.IsNullOrEmpty(key) &&
                 CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(key), Encoding.UTF8.GetBytes(expected));
        if (!ok)
        {
            logger.LogWarning("Geçersiz job anahtarı ile çağrı yapıldı.");
            throw new AppException(ErrorCodes.InvalidJobKey, "Geçersiz anahtar.", StatusCodes.Status403Forbidden);
        }
    }

    public async Task<JobRunResult> RunAsync(CancellationToken ct)
    {
        // Cron üst üste tetiklerse aynı anda iki kez çalışmasın
        if (!await RunLock.WaitAsync(0, ct))
        {
            logger.LogWarning("Job zaten çalışıyor, bu tetikleme atlandı.");
            return new JobRunResult(0, 0, 0, 0, 0, 0, false, 0);
        }

        try
        {
            var (rs, rf) = await RunTaskSafe("Günlük hatırlatma", () => SendDailyRemindersAsync(ct));
            var (ws, wf) = await RunTaskSafe("Haftalık rapor", () => SendWeeklyReportsAsync(ct));
            var (ms, mf) = await RunTaskSafe("Aylık rapor", () => SendMonthlyReportsAsync(ct));
            var (cleaned, ran) = await RunCleanupIfDueAsync(ct);
            var result = new JobRunResult(rs, rf, ws, wf, ms, mf, ran, cleaned);
            logger.LogInformation("Job tamamlandı: {@JobResult}", result);
            return result;
        }
        finally
        {
            RunLock.Release();
        }
    }

    private async Task<(int, int)> RunTaskSafe(string name, Func<Task<(int, int)>> task)
    {
        try
        {
            return await task();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Bir görev çökse bile diğerleri çalışsın
            logger.LogError(ex, "Job görevi hata verdi: {JobTask}", name);
            return (0, 0);
        }
    }

    public async Task SendSampleReportAsync(int userId, string type, CancellationToken ct)
    {
        await using var c = await db.OpenAsync(ct);
        var t = await c.QuerySingleOrDefaultAsync<MailTarget>(new CommandDefinition(
            "SELECT Id AS UserId, Email, FullName FROM auth.Users WHERE Id = @userId AND IsDeleted = 0", new { userId }, cancellationToken: ct))
            ?? throw AppException.NotFound();
        var today = clock.Today;
        string subject, html; MailType mailType;
        switch (type)
        {
            case "daily":
                var due = await study.CountDueAsync(userId, clock.Now, today.AddDays(1), ct);
                var (streak, _, studiedToday) = await statsService.GetStreakAsync(userId, ct);
                (subject, html, mailType) = ("[Örnek] Günlük hatırlatma / özet",
                    await BuildReminderAsync(t.FullName, due, streak, studiedToday, await study.GetDailyStatsAsync(userId, today, ct), ct), MailType.DailyReminder);
                break;
            case "weekly":
                (subject, html, mailType) = ("[Örnek] Haftalık İngilizce raporun 📊", await BuildWeeklyReportAsync(t, today, ct), MailType.WeeklyReport);
                break;
            case "monthly":
                var monthStart = new DateTime(today.Year, today.Month, 1);
                var m = await BuildMonthlyReportAsync(t, monthStart.AddMonths(-1), monthStart.AddDays(-1), ct);
                (subject, html, mailType) = ("[Örnek] " + m.Subject, m.Html, MailType.MonthlyReport);
                break;
            default:
                throw new AppException(ErrorCodes.ValidationError, "Rapor türü daily, weekly veya monthly olmalı.");
        }
        if (!await mail.SendAsync(userId, mailType, t.Email, subject, html, ct))
            throw new AppException(ErrorCodes.MailFailed, "Örnek mail gönderilemedi. SMTP ayarlarını kontrol et (ayrıntı loglarda).");
        logger.LogInformation("Örnek rapor gönderildi: {ReportType}", type);
    }

    // ---------- Günlük hatırlatma ----------

    private async Task<(int Sent, int Failed)> SendDailyRemindersAsync(CancellationToken ct)
    {
        var now = clock.Now;
        var today = clock.Today;
        await using var c = await db.OpenAsync(ct);
        var targets = (await c.QueryAsync<MailTarget>(new CommandDefinition(
            """
            SELECT u.Id AS UserId, u.Email, u.FullName
            FROM study.UserSettings s
            JOIN auth.Users u ON u.Id = s.UserId AND u.IsActive = 1 AND u.IsDeleted = 0
            WHERE s.ReminderEnabled = 1
              AND s.ReminderTime <= @time
              AND (s.LastReminderSentDate IS NULL OR s.LastReminderSentDate < @today)
            """, new { time = now.TimeOfDay, today }, cancellationToken: ct))).ToList();

        int sent = 0, failed = 0;
        foreach (var t in targets)
        {
            try
            {
                var due = await study.CountDueAsync(t.UserId, now, today.AddDays(1), ct);
                var todayStats = await study.GetDailyStatsAsync(t.UserId, today, ct);
                var (streak, _, studiedToday) = await statsService.GetStreakAsync(t.UserId, ct);

                // Bugün çalıştı ve bekleyen kart yoksa: hatırlatma yerine günün özeti
                var subject = studiedToday && due == 0 ? "Bugünün özeti: harika iş çıkardın 🎉"
                    : studiedToday ? "Bugünkü tekrarların bitmedi 💪" : "Bugünkü İngilizce çalışman seni bekliyor 📚";
                var ok = await mail.SendAsync(t.UserId, MailType.DailyReminder, t.Email, subject,
                    await BuildReminderAsync(t.FullName, due, streak, studiedToday, todayStats, ct), ct);

                // Başarısız olsa da bugün için işaretle: her 15 dk'da tekrar deneyip log'u doldurmasın
                await MarkSentAsync(c, t.UserId, "LastReminderSentDate", today, ct);
                if (ok) sent++; else failed++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failed++;
                logger.LogError(ex, "Hatırlatma hazırlanamadı. UserId: {TargetUserId}", t.UserId);
            }
        }
        return (sent, failed);
    }

    // ---------- Haftalık rapor ----------

    private async Task<(int Sent, int Failed)> SendWeeklyReportsAsync(CancellationToken ct)
    {
        var now = clock.Now;
        var today = clock.Today;
        var isoDay = (byte)(((int)today.DayOfWeek + 6) % 7 + 1); // 1=Pzt ... 7=Paz

        await using var c = await db.OpenAsync(ct);
        var targets = (await c.QueryAsync<MailTarget>(new CommandDefinition(
            """
            SELECT u.Id AS UserId, u.Email, u.FullName
            FROM study.UserSettings s
            JOIN auth.Users u ON u.Id = s.UserId AND u.IsActive = 1 AND u.IsDeleted = 0
            WHERE s.WeeklyReportEnabled = 1
              AND s.WeeklyReportDay = @isoDay
              AND s.ReminderTime <= @time
              AND (s.LastWeeklyReportSentDate IS NULL OR s.LastWeeklyReportSentDate < @today)
            """, new { isoDay, time = now.TimeOfDay, today }, cancellationToken: ct))).ToList();

        int sent = 0, failed = 0;
        foreach (var t in targets)
        {
            try
            {
                var html = await BuildWeeklyReportAsync(t, today, ct);
                var ok = await mail.SendAsync(t.UserId, MailType.WeeklyReport, t.Email, "Haftalık İngilizce raporun 📊", html, ct);
                await MarkSentAsync(c, t.UserId, "LastWeeklyReportSentDate", today, ct);
                if (ok) sent++; else failed++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failed++;
                logger.LogError(ex, "Haftalık rapor hazırlanamadı. UserId: {TargetUserId}", t.UserId);
            }
        }
        return (sent, failed);
    }

    // ---------- Aylık rapor (ayın ilk 3 günü, geçen ay için) ----------

    private async Task<(int Sent, int Failed)> SendMonthlyReportsAsync(CancellationToken ct)
    {
        var now = clock.Now;
        var today = clock.Today;
        if (today.Day > 3) return (0, 0); // ayın başında kaçırılırsa (sunucu kapalıydı vb.) 3 gün içinde telafi edilir
        var monthStart = new DateTime(today.Year, today.Month, 1);

        await using var c = await db.OpenAsync(ct);
        var targets = (await c.QueryAsync<MailTarget>(new CommandDefinition(
            """
            SELECT u.Id AS UserId, u.Email, u.FullName
            FROM study.UserSettings s
            JOIN auth.Users u ON u.Id = s.UserId AND u.IsActive = 1 AND u.IsDeleted = 0
            WHERE s.MonthlyReportEnabled = 1
              AND s.ReminderTime <= @time
              AND (s.LastMonthlyReportSentDate IS NULL OR s.LastMonthlyReportSentDate < @monthStart)
            """, new { time = now.TimeOfDay, monthStart }, cancellationToken: ct))).ToList();

        int sent = 0, failed = 0;
        foreach (var t in targets)
        {
            try
            {
                var (subject, html) = await BuildMonthlyReportAsync(t, monthStart.AddMonths(-1), monthStart.AddDays(-1), ct);
                var ok = await mail.SendAsync(t.UserId, MailType.MonthlyReport, t.Email, subject, html, ct);
                await MarkSentAsync(c, t.UserId, "LastMonthlyReportSentDate", today, ct);
                if (ok) sent++; else failed++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failed++;
                logger.LogError(ex, "Aylık rapor hazırlanamadı. UserId: {TargetUserId}", t.UserId);
            }
        }
        return (sent, failed);
    }

    // ---------- Temizlik (günde bir, gece 03:00 sonrası) ----------

    private async Task<(int Rows, bool Ran)> RunCleanupIfDueAsync(CancellationToken ct)
    {
        var now = clock.Now;
        if (now.Hour < 3 || _lastCleanupDate == clock.Today) return (0, false);
        _lastCleanupDate = clock.Today;

        try
        {
            await using var c = await db.OpenAsync(ct);
            var rows = 0;
            rows += await DeleteBatchesAsync(c, "DELETE TOP (5000) FROM app.Logs WHERE TimeStamp < @cutoff", now.AddDays(-90), ct);
            rows += await DeleteBatchesAsync(c, "DELETE TOP (5000) FROM ai.Usage WHERE CreatedAt < @cutoff", now.AddDays(-180), ct);
            rows += await DeleteBatchesAsync(c, "DELETE TOP (5000) FROM app.EmailLogs WHERE CreatedAt < @cutoff", now.AddDays(-180), ct);
            rows += await refreshTokens.DeleteExpiredAsync(now.AddDays(-30), ct);
            logger.LogInformation("Gece temizliği tamamlandı. Silinen satır: {Rows}", rows);
            return (rows, true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Gece temizliği hata verdi.");
            return (0, true);
        }
    }

    private static async Task<int> DeleteBatchesAsync(Microsoft.Data.SqlClient.SqlConnection c, string sql, DateTime cutoff, CancellationToken ct)
    {
        int total = 0, affected;
        do
        {
            affected = await c.ExecuteAsync(new CommandDefinition(sql, new { cutoff }, cancellationToken: ct));
            total += affected;
        } while (affected == 5000);
        return total;
    }

    private static Task MarkSentAsync(Microsoft.Data.SqlClient.SqlConnection c, int userId, string column, DateTime date, CancellationToken ct)
    {
        // column sadece bu sınıftaki sabit değerlerden gelir
        var sql = column switch
        {
            "LastReminderSentDate" => "UPDATE study.UserSettings SET LastReminderSentDate = @date WHERE UserId = @userId",
            "LastMonthlyReportSentDate" => "UPDATE study.UserSettings SET LastMonthlyReportSentDate = @date WHERE UserId = @userId",
            _ => "UPDATE study.UserSettings SET LastWeeklyReportSentDate = @date WHERE UserId = @userId"
        };
        return c.ExecuteAsync(new CommandDefinition(sql, new { userId, date }, cancellationToken: ct));
    }

    // ---------- Mail şablonları ----------

    private async Task<string> AppLinkAsync(string path, CancellationToken ct)
    {
        var baseUrl = (await settings.GetAsync(SettingKeys.AppUrl, ct))?.TrimEnd('/');
        return string.IsNullOrEmpty(baseUrl) ? "" : baseUrl + path;
    }

    private async Task<string> BuildReminderAsync(string name, int due, int streak, bool studiedToday, DailyStatsRow? today, CancellationToken ct)
    {
        var link = await AppLinkAsync("/Study", ct);
        var sb = new StringBuilder();
        sb.Append(Header($"Merhaba {Enc(FirstName(name))} 👋"));
        if (studiedToday && due == 0)
            sb.Append("<p>Bugünkü bütün tekrarlarını bitirdin. Kendine bir aferin de 👏 İşte günün özeti:</p>");
        else
            sb.Append(studiedToday
                ? $"<p>Bugün güzel çalıştın ama hâlâ <b>{due}</b> tekrarın bekliyor. Birkaç dakika ayırırsan günü tamamlarsın.</p>"
                : due > 0 ? $"<p>Bugün henüz çalışmadın. Seni bekleyen <b>{due}</b> tekrar var. 10 dakika bile fark yaratır.</p>"
                        : "<p>Bugün henüz çalışmadın. Yeni kelimeler seni bekliyor; 10 dakika bile fark yaratır.</p>");

        if (studiedToday && today is not null)
        {
            var accuracy = today.ReviewCount == 0 ? 0 : (int)Math.Round(100.0 * today.CorrectCount / today.ReviewCount);
            sb.Append("<table style=\"width:100%;border-collapse:collapse;margin:12px 0\">");
            sb.Append(Row("Çalışılan kart", today.ReviewCount.ToString(Tr)));
            sb.Append(Row("Yeni kelime", today.NewCount.ToString(Tr)));
            sb.Append(Row("Süre", $"{Math.Round(today.StudySeconds / 60.0)} dk"));
            sb.Append(Row("Doğruluk", $"%{accuracy}"));
            sb.Append("</table>");
        }

        sb.Append(streak > 0
            ? (studiedToday ? $"<p>🔥 Serin <b>{streak} gün</b> oldu. Yarın da gel, zinciri kırma!</p>"
                            : $"<p>🔥 <b>{streak} günlük</b> serin var. Bugün çalışırsan seri devam eder!</p>")
            : "<p>Bugün başlarsan yeni bir seri başlatırsın. Küçük ama her gün, en etkili yöntem bu.</p>");
        if (!string.IsNullOrEmpty(link)) sb.Append(Button(link, studiedToday && due == 0 ? "Durumuma bak" : "Çalışmaya başla"));
        sb.Append(Footer());
        return sb.ToString();
    }

    private async Task<string> BuildWeeklyReportAsync(MailTarget t, DateTime today, CancellationToken ct)
    {
        var from = today.AddDays(-6);
        var days = await stats.GetDailyStatsAsync(t.UserId, from, today, ct);
        var reviews = days.Sum(d => d.ReviewCount);
        var correct = days.Sum(d => d.CorrectCount);
        var minutes = (int)Math.Round(days.Sum(d => d.StudySeconds) / 60.0);
        var activeDays = days.Count(d => d.ReviewCount > 0);
        var newWords = await stats.CountCardsCreatedAsync(t.UserId, CardType.WordMeaning, from, today.AddDays(1), ct);
        var counts = await stats.GetWordCountsAsync(t.UserId, ct);
        var struggling = await stats.GetStrugglingWordsAsync(t.UserId, 10, ct);
        var dueTomorrow = await study.CountDueAsync(t.UserId, today.AddDays(1).AddHours(23), today.AddDays(2), ct);
        var accuracy = reviews == 0 ? 0 : (int)Math.Round(100.0 * correct / reviews);
        var (streak, _, _) = await statsService.GetStreakAsync(t.UserId, ct);

        var sb = new StringBuilder();
        sb.Append(Header($"Haftalık raporun, {Enc(FirstName(t.FullName))} 📊"));
        sb.Append($"<p style=\"color:#666\">{from.ToString("d MMMM", Tr)} – {today.ToString("d MMMM yyyy", Tr)}</p>");
        sb.Append("<table style=\"width:100%;border-collapse:collapse;margin:16px 0\">");
        sb.Append(Row("Çalıştığın gün", $"{activeDays} / 7"));
        sb.Append(Row("Toplam süre", $"{minutes} dk"));
        sb.Append(Row("Yeni kelime", newWords.ToString(Tr)));
        sb.Append(Row("Toplam tekrar", reviews.ToString(Tr)));
        sb.Append(Row("Doğruluk", $"%{accuracy}"));
        sb.Append(Row("Seri", $"{streak} gün"));
        sb.Append(Row("Öğrendiğin kelimeler (toplam)", (counts.Learned + counts.Known).ToString(Tr)));
        sb.Append(Row("Yarın tekrar edilecek", dueTomorrow.ToString(Tr)));
        sb.Append("</table>");

        if (struggling.Count > 0)
        {
            sb.Append("<h3 style=\"margin-top:24px\">Zorlandığın kelimeler</h3><ul>");
            foreach (var w in struggling)
                sb.Append($"<li><b>{Enc(w.Lemma)}</b> — {Enc(w.MeaningTr)} <span style=\"color:#999\">({w.Lapses} kez unuttun)</span></li>");
            sb.Append("</ul><p>Bunlara bu hafta biraz daha dikkat et. Cümle içinde sesli tekrar etmek çok işe yarar.</p>");
        }

        // AI koç (üretilemezse rapor koçsuz gider)
        if (activeDays > 0 && await coach.GetOrCreateForUserAsync(t.UserId, ct) is { } report)
        {
            sb.Append("<h3 style=\"margin-top:24px\">🧭 Koçunun yorumu</h3>");
            sb.Append($"<p>{Enc(report.SummaryTr)}</p>");
            if (report.Focus.Count > 0)
            {
                sb.Append("<p><b>Bu hafta odaklan:</b></p><ol>");
                foreach (var f in report.Focus)
                    sb.Append($"<li><b>{Enc(f.TitleTr)}</b> — {Enc(f.WhyTr)}<br><span style=\"color:#555\">👉 {Enc(f.PracticeTr)}</span></li>");
                sb.Append("</ol>");
            }
            if (report.Phrases.Count > 0)
            {
                sb.Append("<p><b>Bu kalıpları öğren:</b></p><ul>");
                foreach (var p in report.Phrases) sb.Append($"<li><b>{Enc(p.En)}</b> — {Enc(p.Tr)}</li>");
                sb.Append("</ul>");
            }
        }
        var link = await AppLinkAsync("/", ct);
        if (!string.IsNullOrEmpty(link)) sb.Append(Button(link, "Durumuma bak"));
        sb.Append(Footer());
        return sb.ToString();
    }

    /// <summary>Aylık değerlendirme: çalışma özeti + seviye + öğrenilen/zorlanılan kelimeler + sıralama + öneriler.</summary>
    public async Task<(string Subject, string Html)> BuildMonthlyReportAsync(MailTarget t, DateTime from, DateTime to, CancellationToken ct)
    {
        var days = await stats.GetDailyStatsAsync(t.UserId, from, to, ct);
        var reviews = days.Sum(d => d.ReviewCount);
        var correct = days.Sum(d => d.CorrectCount);
        var newWords = days.Sum(d => d.NewCount);
        var minutes = (int)Math.Round(days.Sum(d => d.StudySeconds) / 60.0);
        var activeDays = days.Count(d => d.ReviewCount > 0);
        var totalDays = (to - from).Days + 1;
        var accuracy = reviews == 0 ? (int?)null : (int)Math.Round(100.0 * correct / reviews);
        var bestRun = LongestRun(days.Where(d => d.ReviewCount > 0).Select(d => d.StatDate.Date), from, to);

        var counts = await stats.GetWordCountsAsync(t.UserId, ct);
        var vocabulary = counts.Learned + counts.Known;
        var (level, _) = StatsService.EstimateLevel(vocabulary, await stats.GetPublishedWordCountAsync(ct));
        var struggling = await stats.GetStrugglingWordsAsync(t.UserId, 8, ct);

        await using var c = await db.OpenAsync(ct);
        var end = to.AddDays(1);
        var learnedSample = (await c.QueryAsync<(string Lemma, string MeaningTr)>(new CommandDefinition(
            """
            SELECT TOP (10) w.Lemma, w.MeaningTr FROM study.Cards cd JOIN content.Words w ON w.Id = cd.WordId
            WHERE cd.UserId = @userId AND cd.CardType = 1 AND cd.CreatedAt >= @from AND cd.CreatedAt < @end AND cd.State = 2 AND cd.IsLeech = 0
            ORDER BY cd.Stability DESC
            """, new { userId = t.UserId, from, end }, cancellationToken: ct))).ToList();
        var practice = await c.QuerySingleAsync<(int Chats, int Writings, int Quizzes, int? QuizPct, int Dictations, int? DictPct)>(new CommandDefinition(
            """
            SELECT
              (SELECT COUNT(*) FROM ai.Conversations WHERE UserId = @userId AND StartedAt >= @from AND StartedAt < @end),
              (SELECT COUNT(*) FROM ai.WritingSubmissions WHERE UserId = @userId AND CreatedAt >= @from AND CreatedAt < @end),
              (SELECT COUNT(*) FROM study.QuizAttempts WHERE UserId = @userId AND CreatedAt >= @from AND CreatedAt < @end),
              (SELECT CAST(ROUND(100.0 * SUM(CorrectCount) / NULLIF(SUM(TotalCount), 0), 0) AS int) FROM study.QuizAttempts WHERE UserId = @userId AND CreatedAt >= @from AND CreatedAt < @end),
              (SELECT COUNT(*) FROM study.DictationAttempts WHERE UserId = @userId AND CreatedAt >= @from AND CreatedAt < @end),
              (SELECT AVG(CAST(ScorePercent AS int)) FROM study.DictationAttempts WHERE UserId = @userId AND CreatedAt >= @from AND CreatedAt < @end)
            """, new { userId = t.UserId, from, end }, cancellationToken: ct));
        // Sıralama: aktif kullanıcılar arasında aylık puan (kart + 2 × yeni kelime)
        var ranks = (await c.QueryAsync<(int UserId, int Points)>(new CommandDefinition(
            """
            SELECT u.Id, ISNULL(SUM(ds.ReviewCount) + 2 * SUM(ds.NewCount), 0)
            FROM auth.Users u LEFT JOIN study.DailyStats ds ON ds.UserId = u.Id AND ds.StatDate BETWEEN @from AND @to
            WHERE u.IsActive = 1 AND u.IsDeleted = 0 GROUP BY u.Id
            """, new { from, to }, cancellationToken: ct))).ToList();
        var myPoints = ranks.FirstOrDefault(r => r.UserId == t.UserId).Points;
        var rank = 1 + ranks.Count(r => r.Points > myPoints);

        var monthName = from.ToString("MMMM yyyy", Tr);
        var sb = new StringBuilder();
        sb.Append(Header($"{Enc(monthName)} değerlendirmen, {Enc(FirstName(t.FullName))} 📅"));
        sb.Append($"<p>{Enc(MonthlyComment(activeDays, totalDays, minutes, accuracy))}</p>");
        sb.Append("<table style=\"width:100%;border-collapse:collapse;margin:16px 0\">");
        sb.Append(Row("Çalıştığın gün", $"{activeDays} / {totalDays}"));
        sb.Append(Row("Toplam süre", minutes >= 60 ? $"{minutes / 60} sa {minutes % 60} dk" : $"{minutes} dk"));
        sb.Append(Row("Çalışılan kart", reviews.ToString("N0", Tr)));
        sb.Append(Row("Yeni kelime", newWords.ToString("N0", Tr)));
        sb.Append(Row("Doğruluk", accuracy is null ? "-" : $"%{accuracy}"));
        sb.Append(Row("En uzun seri", $"{bestRun} gün"));
        sb.Append(Row("Kelime dağarcığın (toplam)", vocabulary.ToString("N0", Tr)));
        sb.Append(Row("Tahmini seviyen", level));
        if (ranks.Count > 1) sb.Append(Row("Aylık sıralaman", $"{rank}. / {ranks.Count}"));
        sb.Append("</table>");

        if (practice.Chats + practice.Writings + practice.Quizzes + practice.Dictations > 0)
        {
            sb.Append("<h3 style=\"margin-top:24px\">AI pratik</h3><ul>");
            if (practice.Chats > 0) sb.Append($"<li>{practice.Chats} sohbet</li>");
            if (practice.Writings > 0) sb.Append($"<li>{practice.Writings} yazı</li>");
            if (practice.Quizzes > 0) sb.Append($"<li>{practice.Quizzes} dinleme testi" + (practice.QuizPct is { } q ? $" (ortalama %{q})" : "") + "</li>");
            if (practice.Dictations > 0) sb.Append($"<li>{practice.Dictations} dikte" + (practice.DictPct is { } d ? $" (ortalama %{d})" : "") + "</li>");
            sb.Append("</ul>");
        }
        if (learnedSample.Count > 0)
        {
            sb.Append("<h3 style=\"margin-top:24px\">Bu ay öğrendiklerinden</h3><p>");
            sb.Append(string.Join(" · ", learnedSample.Select(w => $"<b>{Enc(w.Lemma)}</b> ({Enc(w.MeaningTr)})")));
            sb.Append("</p>");
        }
        if (struggling.Count > 0)
        {
            sb.Append("<h3 style=\"margin-top:24px\">Zorlandıkların</h3><ul>");
            foreach (var w in struggling) sb.Append($"<li><b>{Enc(w.Lemma)}</b> — {Enc(w.MeaningTr)} <span style=\"color:#999\">({w.Lapses} kez unuttun)</span></li>");
            sb.Append("</ul><p>Bu kelimeleri cümle içinde sesli tekrar et, dikte ile kulağını alıştır.</p>");
        }

        var link = await AppLinkAsync("/", ct);
        if (!string.IsNullOrEmpty(link)) sb.Append(Button(link, "Bu aya başla"));
        sb.Append(Footer());
        return ($"{monthName} İngilizce değerlendirmen 📅", sb.ToString());
    }

    /// <summary>Ay içindeki en uzun art arda çalışma (gün).</summary>
    public static int LongestRun(IEnumerable<DateTime> studiedDays, DateTime from, DateTime to)
    {
        var set = studiedDays.Select(d => d.Date).ToHashSet();
        int best = 0, run = 0;
        for (var d = from.Date; d <= to.Date; d = d.AddDays(1))
        {
            run = set.Contains(d) ? run + 1 : 0;
            best = Math.Max(best, run);
        }
        return best;
    }

    /// <summary>Aya göre kısa, samimi değerlendirme (AI gerekmez).</summary>
    public static string MonthlyComment(int activeDays, int totalDays, int minutes, int? accuracy)
    {
        if (activeDays == 0) return "Geçen ay hiç çalışamadın. Sorun değil, yeni ay yeni başlangıç: günde 10 dakikayla başla.";
        var ratio = (double)activeDays / totalDays;
        var part1 = ratio >= 0.8 ? "Muhteşem bir ay! Neredeyse her gün çalıştın, düzenli çalışmanın meyvesini göreceksin."
            : ratio >= 0.5 ? "İyi bir ay geçirdin. Haftada birkaç gün daha eklersen ilerleme hızın belirgin artar."
            : "Bu ay biraz aralıklı çalıştın. Hedefin haftada en az 5 gün olsun; kısa ama düzenli çalışmak en etkilisi.";
        var part2 = accuracy is >= 85 ? " Doğruluğun çok yüksek, yeni kelime sayısını artırabilirsin."
            : accuracy is < 65 ? " Doğruluğun biraz düşük; yeni kelime yerine bir süre tekrarlara ağırlık ver."
            : "";
        var part3 = minutes / Math.Max(activeDays, 1) < 15 ? " Çalıştığın günlerde süre kısa kalmış, 20-30 dakikaya çıkarmayı dene." : "";
        return part1 + part2 + part3;
    }

    private static readonly CultureInfo Tr = new("tr-TR");
    private static string Enc(string s) => WebUtility.HtmlEncode(s);
    private static string FirstName(string full) => full.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? full;

    private static string Header(string title) =>
        "<div style=\"font-family:Segoe UI,Arial,sans-serif;max-width:560px;margin:auto;color:#222\">" +
        $"<h2 style=\"color:#4f46e5\">{title}</h2>";

    private static string Row(string label, string value) =>
        $"<tr><td style=\"padding:8px;border-bottom:1px solid #eee\">{label}</td>" +
        $"<td style=\"padding:8px;border-bottom:1px solid #eee;text-align:right\"><b>{value}</b></td></tr>";

    private static string Button(string url, string text) =>
        $"<p style=\"margin:24px 0\"><a href=\"{Enc(url)}\" style=\"background:#4f46e5;color:#fff;padding:12px 20px;" +
        $"border-radius:8px;text-decoration:none;display:inline-block\">{text}</a></p>";

    private static string Footer() =>
        "<p style=\"color:#999;font-size:12px;margin-top:32px\">Bu maili almak istemiyorsan Profil &gt; Bildirimler ekranından kapatabilirsin.</p></div>";
}
