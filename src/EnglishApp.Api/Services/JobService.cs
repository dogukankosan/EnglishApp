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

public sealed record JobRunResult(int RemindersSent, int RemindersFailed, int WeeklySent, int WeeklyFailed, bool CleanupRan, int CleanedRows);

/// <summary>
/// Dış cron servisi (ör. cron-job.org) 15 dakikada bir /api/v1/jobs/run çağırır.
/// IIS uykuya geçse bile hatırlatma ve raporlar böylece zamanında gider.
/// </summary>
public interface IJobService
{
    Task ValidateKeyAsync(string? key, CancellationToken ct);
    Task<JobRunResult> RunAsync(CancellationToken ct);
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
    private sealed class MailTarget
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
            return new JobRunResult(0, 0, 0, 0, false, 0);
        }

        try
        {
            var (rs, rf) = await RunTaskSafe("Günlük hatırlatma", () => SendDailyRemindersAsync(ct));
            var (ws, wf) = await RunTaskSafe("Haftalık rapor", () => SendWeeklyReportsAsync(ct));
            var (cleaned, ran) = await RunCleanupIfDueAsync(ct);
            var result = new JobRunResult(rs, rf, ws, wf, ran, cleaned);
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

                // Bugün zaten çalıştı ve bekleyen kart yoksa rahatsız etme
                if (studiedToday && due == 0)
                {
                    await MarkSentAsync(c, t.UserId, "LastReminderSentDate", today, ct);
                    continue;
                }

                var ok = await mail.SendAsync(t.UserId, MailType.DailyReminder, t.Email,
                    studiedToday ? "Bugünkü tekrarların bitmedi 💪" : "Bugünkü İngilizce çalışman seni bekliyor 📚",
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
        var sql = column == "LastReminderSentDate"
            ? "UPDATE study.UserSettings SET LastReminderSentDate = @date WHERE UserId = @userId"
            : "UPDATE study.UserSettings SET LastWeeklyReportSentDate = @date WHERE UserId = @userId";
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
        sb.Append(studiedToday
            ? $"<p>Bugün <b>{today?.ReviewCount ?? 0}</b> kart çalıştın, harika! Ama hâlâ <b>{due}</b> tekrarın bekliyor.</p>"
            : $"<p>Bugün henüz çalışmadın. Seni bekleyen <b>{due}</b> tekrar var.</p>");
        sb.Append(streak > 0
            ? $"<p>🔥 <b>{streak} günlük</b> serin var. Bugün çalışırsan seri devam eder!</p>"
            : "<p>Bugün başlarsan yeni bir seri başlatırsın. Küçük ama her gün, en etkili yöntem bu.</p>");
        if (!string.IsNullOrEmpty(link)) sb.Append(Button(link, "Çalışmaya başla"));
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
