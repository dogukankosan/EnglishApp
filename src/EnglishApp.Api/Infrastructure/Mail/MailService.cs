using System.Diagnostics;
using Dapper;
using EnglishApp.Api.Infrastructure.Data;
using EnglishApp.Api.Infrastructure.Errors;
using EnglishApp.Api.Infrastructure.Logging;
using EnglishApp.Api.Infrastructure.Settings;
using EnglishApp.Api.Infrastructure.Time;
using EnglishApp.Shared.Enums;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace EnglishApp.Api.Infrastructure.Mail;

/// <summary>SMTP bağlantı bilgileri (kayıtlı ayarlardan ya da kaydedilmeden önce test için formdan).</summary>
public sealed record SmtpConfig(
    string? Host, int Port, bool UseSsl, string? UserName, string? Password,
    string? FromAddress, string? FromName, string? SignatureHtml)
{
    public bool IsComplete => !string.IsNullOrWhiteSpace(Host) && !string.IsNullOrWhiteSpace(FromAddress);
}

public interface IMailService
{
    /// <summary>Mail gönderir, sonucu app.EmailLogs'a yazar. Hata fırlatmaz, başarıyı döner.</summary>
    Task<bool> SendAsync(int? userId, MailType type, string to, string subject, string htmlBody, CancellationToken ct = default);

    /// <summary>Hata olursa AppException fırlatır (admin ekranında mesaj görünsün diye).</summary>
    Task SendOrThrowAsync(int? userId, MailType type, string to, string subject, string htmlBody, CancellationToken ct = default);

    /// <summary>Verilen (henüz kaydedilmemiş olabilir) ayarlarla gönderir. Ayar kaydetmeden önceki test için.</summary>
    Task SendWithConfigOrThrowAsync(SmtpConfig config, int? userId, MailType type, string to, string subject, string htmlBody, CancellationToken ct = default);

    Task<SmtpConfig> LoadConfigAsync(CancellationToken ct = default);
}

public sealed class MailService(
    ISettingsService settings,
    IDbConnectionFactory db,
    ITrClock clock,
    ILogger<MailService> logger) : IMailService
{
    public async Task<bool> SendAsync(int? userId, MailType type, string to, string subject, string htmlBody, CancellationToken ct = default)
    {
        try
        {
            await SendOrThrowAsync(userId, type, to, subject, htmlBody, ct);
            return true;
        }
        catch (AppException)
        {
            return false; // zaten loglandı
        }
    }

    public async Task SendOrThrowAsync(int? userId, MailType type, string to, string subject, string htmlBody, CancellationToken ct = default) =>
        await SendWithConfigOrThrowAsync(await LoadConfigAsync(ct), userId, type, to, subject, htmlBody, ct);

    public async Task<SmtpConfig> LoadConfigAsync(CancellationToken ct = default) => new(
        await settings.GetAsync(SettingKeys.SmtpHost, ct),
        await settings.GetIntAsync(SettingKeys.SmtpPort, 587, ct),
        await settings.GetBoolAsync(SettingKeys.SmtpUseSsl, true, ct),
        await settings.GetAsync(SettingKeys.SmtpUserName, ct),
        await settings.GetAsync(SettingKeys.SmtpPassword, ct),
        await settings.GetAsync(SettingKeys.SmtpFromAddress, ct),
        await settings.GetAsync(SettingKeys.SmtpFromName, ct),
        await settings.GetAsync(SettingKeys.SmtpSignatureHtml, ct));

    public async Task SendWithConfigOrThrowAsync(SmtpConfig cfg, int? userId, MailType type, string to, string subject, string htmlBody, CancellationToken ct = default)
    {
        if (!cfg.IsComplete)
        {
            logger.LogWarning("SMTP ayarları eksik, mail gönderilemedi. Tip: {MailType}", type);
            await LogAsync(userId, type, to, subject, MailStatus.Failed, "SMTP ayarları eksik.", null);
            throw new AppException(ErrorCodes.MailNotConfigured, "SMTP ayarları eksik: en az sunucu ve gönderen adresi girilmeli.");
        }

        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(string.IsNullOrWhiteSpace(cfg.FromName) ? "EnglishApp" : cfg.FromName, cfg.FromAddress!));
        message.To.Add(MailboxAddress.Parse(to));
        message.Subject = subject;
        message.Body = new BodyBuilder { HtmlBody = AppendSignature(htmlBody, cfg.SignatureHtml) }.ToMessageBody();

        var sw = Stopwatch.StartNew();
        try
        {
            using var client = new SmtpClient { Timeout = 20000 };
            var socket = cfg.Port == 465 ? SecureSocketOptions.SslOnConnect
                : cfg.UseSsl ? SecureSocketOptions.StartTls
                : SecureSocketOptions.Auto;

            await client.ConnectAsync(cfg.Host!, cfg.Port, socket, ct);
            if (!string.IsNullOrEmpty(cfg.UserName))
                await client.AuthenticateAsync(cfg.UserName, cfg.Password ?? "", ct);
            await client.SendAsync(message, ct);
            await client.DisconnectAsync(true, ct);

            sw.Stop();
            logger.LogInformation("Mail gönderildi. Tip: {MailType}, Alıcı: {To}, Süre: {DurationMs} ms",
                type, LogSanitizer.MaskEmail(to), sw.ElapsedMilliseconds);
            await LogAsync(userId, type, to, subject, MailStatus.Sent, null, (int)sw.ElapsedMilliseconds);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            sw.Stop();
            logger.LogError(ex, "Mail gönderilemedi. Tip: {MailType}, Alıcı: {To}, Sunucu: {SmtpHost}:{SmtpPort}",
                type, LogSanitizer.MaskEmail(to), cfg.Host, cfg.Port);
            await LogAsync(userId, type, to, subject, MailStatus.Failed, ex.Message, (int)sw.ElapsedMilliseconds);
            throw new AppException(ErrorCodes.MailFailed, $"Mail gönderilemedi: {ex.Message}");
        }
    }

    /// <summary>İmzayı mailin en altına ekler (gövde kendi div'iyle bitiyorsa onun içine değil, altına).</summary>
    public static string AppendSignature(string htmlBody, string? signatureHtml) =>
        string.IsNullOrWhiteSpace(signatureHtml)
            ? htmlBody
            : htmlBody +
              "<div style=\"font-family:Segoe UI,Arial,sans-serif;max-width:560px;margin:24px auto 0;padding-top:12px;border-top:1px solid #e5e7eb\">" +
              signatureHtml + "</div>";

    private async Task LogAsync(int? userId, MailType type, string to, string subject, MailStatus status, string? error, int? durationMs)
    {
        try
        {
            await using var conn = await db.OpenAsync();
            await conn.ExecuteAsync(
                """
                INSERT INTO app.EmailLogs (UserId, MailType, ToAddress, Subject, Status, ErrorMessage, DurationMs, CreatedAt)
                VALUES (@UserId, @MailType, @To, @Subject, @Status, @Error, @DurationMs, @Now)
                """,
                new
                {
                    UserId = userId, MailType = (byte)type, To = to, Subject = Truncate(subject, 300),
                    Status = (byte)status, Error = Truncate(error, 1000), DurationMs = durationMs, Now = clock.Now
                });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "EmailLogs kaydı yazılamadı.");
        }
    }

    private static string? Truncate(string? s, int max) => s is null || s.Length <= max ? s : s[..max];
}
