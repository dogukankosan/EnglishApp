using System.Text;
using Dapper;
using EnglishApp.Api.Data;
using EnglishApp.Api.Infrastructure.Data;
using EnglishApp.Api.Infrastructure.Errors;
using EnglishApp.Api.Infrastructure.Logging;
using EnglishApp.Api.Infrastructure.Mail;
using EnglishApp.Api.Infrastructure.Security;
using EnglishApp.Api.Infrastructure.Settings;
using EnglishApp.Api.Infrastructure.Time;
using EnglishApp.Api.Infrastructure.Validation;
using EnglishApp.Shared.Common;
using EnglishApp.Shared.Dtos;
using EnglishApp.Shared.Enums;
using FluentValidation;

namespace EnglishApp.Api.Services;

public interface IAdminUserService
{
    Task<IReadOnlyList<AdminUserDto>> ListAsync(CancellationToken ct);
    Task<AdminUserDto> GetAsync(int id, CancellationToken ct);
    Task<int> CreateAsync(CreateUserRequest req, CancellationToken ct);
    Task UpdateAsync(int id, UpdateUserRequest req, CancellationToken ct);
    Task SetActiveAsync(int id, bool isActive, CancellationToken ct);
    Task DeleteAsync(int id, CancellationToken ct);
    Task ResetPasswordAsync(int id, ResetPasswordRequest req, CancellationToken ct);
    Task UnlockAsync(int id, CancellationToken ct);
}

public sealed class AdminUserService(
    IUserRepository users,
    IRefreshTokenRepository refreshTokens,
    IPasswordService passwords,
    IAuthStateCache authCache,
    ISettingsService settings,
    IAuditService audit,
    ICurrentUser currentUser,
    ITrClock clock) : IAdminUserService
{
    public Task<IReadOnlyList<AdminUserDto>> ListAsync(CancellationToken ct) =>
        users.ListForAdminAsync(clock.Today, clock.Now, ct);

    public async Task<AdminUserDto> GetAsync(int id, CancellationToken ct) =>
        (await ListAsync(ct)).FirstOrDefault(u => u.Id == id) ?? throw AppException.NotFound("Kullanıcı bulunamadı.");

    public async Task<int> CreateAsync(CreateUserRequest req, CancellationToken ct)
    {
        var email = AuthService.NormalizeEmail(req.Email);
        if (await users.EmailExistsAsync(email, null, ct))
            throw AppException.Conflict(ErrorCodes.EmailInUse, "Bu e-posta adresi zaten kullanılıyor.");

        var tokenLimit = req.DailyTokenLimit ?? await settings.GetIntAsync(SettingKeys.DefaultDailyTokenLimit, 50000, ct);
        var id = await users.CreateAsync(new UserRow
        {
            Email = email,
            FullName = req.FullName.Trim(),
            PasswordHash = passwords.Hash(req.Password),
            RoleId = req.Role,
            DailyTokenLimit = tokenLimit,
            MustChangePassword = true
        }, currentUser.Id, clock.Now, ct);

        await audit.WriteAsync("UserCreated", "User", id, null,
            new { Email = email, req.FullName, Role = req.Role.ToString(), DailyTokenLimit = tokenLimit });
        return id;
    }

    public async Task UpdateAsync(int id, UpdateUserRequest req, CancellationToken ct)
    {
        var user = await users.GetByIdAsync(id, ct) ?? throw AppException.NotFound("Kullanıcı bulunamadı.");
        if (id == currentUser.Id && req.Role != user.RoleId)
            throw new AppException(ErrorCodes.CannotModifySelf, "Kendi rolünüzü değiştiremezsiniz.");

        req.Email = AuthService.NormalizeEmail(req.Email);
        req.FullName = req.FullName.Trim();
        if (await users.EmailExistsAsync(req.Email, id, ct))
            throw AppException.Conflict(ErrorCodes.EmailInUse, "Bu e-posta adresi başka bir kullanıcıda kayıtlı.");

        await users.UpdateByAdminAsync(id, req, currentUser.Id, clock.Now, ct);
        if (req.Role != user.RoleId) authCache.Invalidate(id);

        await audit.WriteAsync("UserUpdated", "User", id,
            new { user.Email, user.FullName, Role = user.RoleId.ToString(), user.DailyTokenLimit },
            new { req.Email, req.FullName, Role = req.Role.ToString(), req.DailyTokenLimit });
    }

    public async Task SetActiveAsync(int id, bool isActive, CancellationToken ct)
    {
        if (id == currentUser.Id)
            throw new AppException(ErrorCodes.CannotModifySelf, "Kendi hesabınızı pasife alamazsınız.");
        var user = await users.GetByIdAsync(id, ct) ?? throw AppException.NotFound("Kullanıcı bulunamadı.");
        if (user.IsActive == isActive) return;

        var now = clock.Now;
        await users.SetActiveAsync(id, isActive, currentUser.Id, now, ct);
        if (!isActive) await refreshTokens.RevokeAllForUserAsync(id, RevokeReason.AdminAction, now, ct);
        authCache.Invalidate(id);

        await audit.WriteAsync(isActive ? "UserActivated" : "UserDeactivated", "User", id,
            new { IsActive = user.IsActive }, new { IsActive = isActive });
    }

    public async Task DeleteAsync(int id, CancellationToken ct)
    {
        if (id == currentUser.Id)
            throw new AppException(ErrorCodes.CannotModifySelf, "Kendi hesabınızı silemezsiniz.");
        var user = await users.GetByIdAsync(id, ct) ?? throw AppException.NotFound("Kullanıcı bulunamadı.");

        var now = clock.Now;
        await users.SoftDeleteAsync(id, currentUser.Id, now, ct);
        await refreshTokens.RevokeAllForUserAsync(id, RevokeReason.AdminAction, now, ct);
        authCache.Invalidate(id);

        await audit.WriteAsync("UserDeleted", "User", id, new { user.Email, user.FullName }, null);
    }

    public async Task ResetPasswordAsync(int id, ResetPasswordRequest req, CancellationToken ct)
    {
        _ = await users.GetByIdAsync(id, ct) ?? throw AppException.NotFound("Kullanıcı bulunamadı.");
        var now = clock.Now;
        await users.UpdatePasswordAsync(id, passwords.Hash(req.NewPassword), mustChange: true, currentUser.Id, now, ct);
        await refreshTokens.RevokeAllForUserAsync(id, RevokeReason.AdminAction, now, ct);
        authCache.Invalidate(id);

        await audit.WriteAsync("UserPasswordReset", "User", id, null, new { MustChangePassword = true });
    }

    public async Task UnlockAsync(int id, CancellationToken ct)
    {
        _ = await users.GetByIdAsync(id, ct) ?? throw AppException.NotFound("Kullanıcı bulunamadı.");
        await users.UnlockAsync(id, ct);
        await audit.WriteAsync("UserUnlocked", "User", id);
    }
}

// ---------- Sistem ayarları ----------

public interface IAdminSettingsService
{
    Task<IReadOnlyList<SettingDto>> ListAsync(CancellationToken ct);
    Task<SaveSettingsResultDto> UpdateAsync(SaveSettingsRequest req, CancellationToken ct);
    Task SendTestMailAsync(TestMailRequest req, CancellationToken ct);
}

public sealed class AdminSettingsService(
    ISettingsService settings,
    IMailService mail,
    IUserRepository users,
    IAuditService audit,
    ICurrentUser currentUser,
    ILogger<AdminSettingsService> logger) : IAdminSettingsService
{
    public async Task<IReadOnlyList<SettingDto>> ListAsync(CancellationToken ct) =>
        (await settings.GetAllRawAsync(ct)).Select(s => new SettingDto
        {
            Key = s.Key,
            Value = s.IsEncrypted ? null : s.Value,   // şifreli değer asla geri dönmez
            IsEncrypted = s.IsEncrypted,
            HasValue = !string.IsNullOrEmpty(s.Value),
            GroupName = s.GroupName,
            DescriptionTr = s.DescriptionTr,
            UpdatedAt = s.UpdatedAt
        }).ToList();

    public async Task<SaveSettingsResultDto> UpdateAsync(SaveSettingsRequest req, CancellationToken ct)
    {
        var existing = (await settings.GetAllRawAsync(ct)).ToDictionary(s => s.Key, StringComparer.OrdinalIgnoreCase);
        foreach (var item in req.Items)
            if (!existing.ContainsKey(item.Key))
                throw new AppException(ErrorCodes.SettingNotFound, $"Ayar bulunamadı: {item.Key}", StatusCodes.Status404NotFound);

        // Şifreli alan boş gönderildiyse "değiştirme" demek (ekranda değer gösterilmiyor)
        var items = req.Items.Where(i => !(existing[i.Key].IsEncrypted && i.Value is null)).ToList();
        var result = new SaveSettingsResultDto();

        // KURAL: SMTP ayarı değiştiyse, yeni ayarlarla test maili BAŞARILI olmadan hiçbir şey kaydedilmez
        if (items.Any(i => i.Key.StartsWith("Smtp.", StringComparison.OrdinalIgnoreCase)))
        {
            var current = await mail.LoadConfigAsync(ct);
            string? New(string key, string? currentValue) =>
                items.FirstOrDefault(i => i.Key.Equals(key, StringComparison.OrdinalIgnoreCase)) is { } i
                    ? (string.IsNullOrWhiteSpace(i.Value) ? null : i.Value.Trim())
                    : currentValue;

            var portText = New(SettingKeys.SmtpPort, current.Port.ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (!int.TryParse(portText, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var port) || port is < 1 or > 65535)
                throw new AppException(ErrorCodes.ValidationError, "SMTP port 1-65535 arasında bir sayı olmalı.");

            var candidate = new SmtpConfig(
                New(SettingKeys.SmtpHost, current.Host),
                port,
                !string.Equals(New(SettingKeys.SmtpUseSsl, current.UseSsl ? "true" : "false"), "false", StringComparison.OrdinalIgnoreCase),
                New(SettingKeys.SmtpUserName, current.UserName),
                items.FirstOrDefault(i => i.Key == SettingKeys.SmtpPassword)?.Value ?? current.Password, // şifre trim edilmez
                New(SettingKeys.SmtpFromAddress, current.FromAddress),
                New(SettingKeys.SmtpFromName, current.FromName),
                items.FirstOrDefault(i => i.Key == SettingKeys.SmtpSignatureHtml) is { } sig ? sig.Value : current.SignatureHtml);

            var to = string.IsNullOrWhiteSpace(req.TestMailTo)
                ? (await users.GetByIdAsync(currentUser.Id, ct))?.Email
                : req.TestMailTo.Trim();
            if (string.IsNullOrWhiteSpace(to))
                throw new AppException(ErrorCodes.ValidationError, "Test maili için bir adres gerekli.");

            await mail.SendWithConfigOrThrowAsync(candidate, currentUser.Id, MailType.Test, to,
                "EnglishApp - SMTP ayar testi", TestMailBody(), ct);

            logger.LogInformation("SMTP test maili başarılı, ayarlar kaydediliyor.");
            result.TestMailSent = true;
            result.TestMailTo = to;
        }

        foreach (var item in items)
        {
            var row = existing[item.Key];
            await settings.SetAsync(item.Key, item.Value, currentUser.Id, ct);
            result.SavedCount++;
            await audit.WriteAsync("SettingChanged", "Setting", item.Key,
                new { Value = row.IsEncrypted ? "***" : row.Value },
                new { Value = row.IsEncrypted ? "***" : item.Value });
        }
        return result;
    }

    public async Task SendTestMailAsync(TestMailRequest req, CancellationToken ct) =>
        await mail.SendOrThrowAsync(currentUser.Id, MailType.Test, req.ToAddress.Trim(), "EnglishApp - Test Maili", TestMailBody(), ct);

    private static string TestMailBody() => new StringBuilder()
        .Append("<div style=\"font-family:Segoe UI,Arial,sans-serif;max-width:560px;margin:auto\">")
        .Append("<h2 style=\"color:#4f46e5\">EnglishApp test maili ✅</h2>")
        .Append("<p>SMTP ayarların doğru çalışıyor. Hatırlatma ve rapor mailleri bu ayarlarla gönderilecek.</p></div>").ToString();
}

// ---------- Log görüntüleme ----------

public interface ILogQueryService
{
    Task<PagedResult<LogItemDto>> QueryAsync(LogQuery q, CancellationToken ct);
    Task<(byte[] Content, string FileName, int Rows)> ExportExcelAsync(LogQuery q, CancellationToken ct);
}

public sealed class LogQueryService(IDbConnectionFactory db, ITrClock clock, ILogger<LogQueryService> logger) : ILogQueryService
{
    public const int MaxExportRows = 20000;

    /// <summary>Filtrelenmiş logları Excel (.xlsx) dosyası olarak üretir. En fazla 20.000 satır.</summary>
    public async Task<(byte[] Content, string FileName, int Rows)> ExportExcelAsync(LogQuery q, CancellationToken ct)
    {
        var (where, p) = BuildWhere(q);
        p.Add("Max", MaxExportRows);

        await using var c = await db.OpenAsync(ct);
        var rows = (await c.QueryAsync<LogItemDto>(new CommandDefinition(
            $"""
            SELECT TOP (@Max) Id, TimeStamp, Level, SourceApp, Message, Exception, Properties, CorrelationId, UserId,
                   RequestPath, RequestMethod, ClientIp
            FROM app.Logs {where}
            ORDER BY TimeStamp DESC, Id DESC
            """, p, cancellationToken: ct, commandTimeout: 120))).ToList();

        using var wb = new ClosedXML.Excel.XLWorkbook();
        var ws = wb.Worksheets.Add("Loglar");
        string[] headers = ["Zaman", "Seviye", "Kaynak", "Mesaj", "Hata (Exception)", "Correlation ID", "Kullanıcı", "Metot", "İstek", "IP"];
        for (var i = 0; i < headers.Length; i++) ws.Cell(1, i + 1).Value = headers[i];

        var r = 2;
        foreach (var l in rows)
        {
            ws.Cell(r, 1).Value = l.TimeStamp;
            ws.Cell(r, 1).Style.DateFormat.Format = "dd.MM.yyyy HH:mm:ss";
            ws.Cell(r, 2).Value = l.Level;
            ws.Cell(r, 3).Value = l.SourceApp;
            ws.Cell(r, 4).Value = Cut(l.Message);
            ws.Cell(r, 5).Value = Cut(l.Exception);
            ws.Cell(r, 6).Value = l.CorrelationId;
            if (l.UserId is not null) ws.Cell(r, 7).Value = l.UserId.Value;
            ws.Cell(r, 8).Value = l.RequestMethod;
            ws.Cell(r, 9).Value = l.RequestPath;
            ws.Cell(r, 10).Value = l.ClientIp;
            if (l.Level is "Error" or "Fatal") ws.Range(r, 1, r, 3).Style.Font.FontColor = ClosedXML.Excel.XLColor.Red;
            r++;
        }

        var header = ws.Range(1, 1, 1, headers.Length);
        header.Style.Font.Bold = true;
        header.Style.Fill.BackgroundColor = ClosedXML.Excel.XLColor.FromHtml("#4F46E5");
        header.Style.Font.FontColor = ClosedXML.Excel.XLColor.White;
        ws.SheetView.FreezeRows(1);
        if (rows.Count > 0) ws.Range(1, 1, rows.Count + 1, headers.Length).SetAutoFilter();
        ws.Columns(1, 3).AdjustToContents();
        ws.Columns(6, 10).AdjustToContents();
        ws.Column(4).Width = 80;
        ws.Column(5).Width = 60;

        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        logger.LogInformation("Loglar Excel'e aktarıldı: {Rows} satır", rows.Count);
        return (ms.ToArray(), $"loglar_{clock.Now:yyyy-MM-dd_HHmm}.xlsx", rows.Count);
    }

    // Excel hücresi en fazla 32.767 karakter alır
    private static string? Cut(string? s) => s is null || s.Length <= 32000 ? s : s[..32000] + "…";

    private static (string Where, DynamicParameters Params) BuildWhere(LogQuery q)
    {
        var where = new StringBuilder("WHERE 1 = 1");
        var p = new DynamicParameters();
        if (!string.IsNullOrWhiteSpace(q.Level)) { where.Append(" AND Level = @Level"); p.Add("Level", q.Level); }
        if (!string.IsNullOrWhiteSpace(q.SourceApp)) { where.Append(" AND SourceApp = @SourceApp"); p.Add("SourceApp", q.SourceApp); }
        if (q.From is not null) { where.Append(" AND TimeStamp >= @From"); p.Add("From", q.From); }
        if (q.To is not null) { where.Append(" AND TimeStamp < @To"); p.Add("To", q.To.Value.Date.AddDays(1)); }
        if (q.UserId is not null) { where.Append(" AND UserId = @UserId"); p.Add("UserId", q.UserId); }
        if (!string.IsNullOrWhiteSpace(q.CorrelationId)) { where.Append(" AND CorrelationId = @CorrelationId"); p.Add("CorrelationId", q.CorrelationId.Trim()); }
        if (!string.IsNullOrWhiteSpace(q.Search)) { where.Append(" AND (Message LIKE @Search OR Exception LIKE @Search)"); p.Add("Search", $"%{q.Search.Trim()}%"); }
        return (where.ToString(), p);
    }

    public async Task<PagedResult<LogItemDto>> QueryAsync(LogQuery q, CancellationToken ct)
    {
        var page = Math.Max(1, q.Page);
        var size = Math.Clamp(q.PageSize, 10, 200);
        var (where, p) = BuildWhere(q);

        p.Add("Offset", (page - 1) * size);
        p.Add("Size", size);

        // Sadece sabit SQL parçaları birleştiriliyor; kullanıcı değerleri hep parametre
        var sql = $"""
            SELECT COUNT(*) FROM app.Logs {where};
            SELECT Id, TimeStamp, Level, SourceApp, Message, Exception, Properties, CorrelationId, UserId,
                   RequestPath, RequestMethod, ClientIp
            FROM app.Logs {where}
            ORDER BY TimeStamp DESC, Id DESC
            OFFSET @Offset ROWS FETCH NEXT @Size ROWS ONLY;
            """;

        await using var c = await db.OpenAsync(ct);
        using var multi = await c.QueryMultipleAsync(new CommandDefinition(sql, p, cancellationToken: ct));
        var total = await multi.ReadSingleAsync<int>();
        var items = (await multi.ReadAsync<LogItemDto>()).ToList();
        return new PagedResult<LogItemDto>(items, total, page, size);
    }
}

// ---------- Doğrulayıcılar ----------

public sealed class CreateUserRequestValidator : AbstractValidator<CreateUserRequest>
{
    public CreateUserRequestValidator()
    {
        RuleFor(x => x.Email).ValidEmail();
        RuleFor(x => x.FullName).ValidFullName();
        RuleFor(x => x.Role).IsInEnum().WithMessage("Geçersiz rol.");
        RuleFor(x => x.Password).StrongPassword();
        RuleFor(x => x.DailyTokenLimit).GreaterThanOrEqualTo(0).When(x => x.DailyTokenLimit.HasValue)
            .WithMessage("Token limiti negatif olamaz.");
    }
}

public sealed class UpdateUserRequestValidator : AbstractValidator<UpdateUserRequest>
{
    public UpdateUserRequestValidator()
    {
        RuleFor(x => x.Email).ValidEmail();
        RuleFor(x => x.FullName).ValidFullName();
        RuleFor(x => x.Role).IsInEnum().WithMessage("Geçersiz rol.");
        RuleFor(x => x.DailyTokenLimit).GreaterThanOrEqualTo(0).When(x => x.DailyTokenLimit.HasValue)
            .WithMessage("Token limiti negatif olamaz.");
    }
}

public sealed class ResetPasswordRequestValidator : AbstractValidator<ResetPasswordRequest>
{
    public ResetPasswordRequestValidator() => RuleFor(x => x.NewPassword).StrongPassword();
}

public sealed class SaveSettingsRequestValidator : AbstractValidator<SaveSettingsRequest>
{
    public SaveSettingsRequestValidator()
    {
        RuleFor(x => x.Items).NotEmpty().WithMessage("Güncellenecek ayar yok.");
        RuleForEach(x => x.Items).ChildRules(i =>
        {
            i.RuleFor(s => s.Key).NotEmpty().MaximumLength(100);
            i.RuleFor(s => s.Value).MaximumLength(8000).WithMessage("Ayar değeri çok uzun (en fazla 8000 karakter).");
        });
        RuleFor(x => x.TestMailTo).EmailAddress().When(x => !string.IsNullOrWhiteSpace(x.TestMailTo))
            .WithMessage("Test maili adresi geçersiz.");
    }
}

public sealed class TestMailRequestValidator : AbstractValidator<TestMailRequest>
{
    public TestMailRequestValidator() => RuleFor(x => x.ToAddress).ValidEmail();
}

public sealed class LogQueryValidator : AbstractValidator<LogQuery>
{
    private static readonly string[] Levels = ["Verbose", "Debug", "Information", "Warning", "Error", "Fatal"];

    public LogQueryValidator()
    {
        RuleFor(x => x.Level).Must(l => l is null || Levels.Contains(l)).WithMessage("Geçersiz log seviyesi.");
        RuleFor(x => x.Search).MaximumLength(200);
        RuleFor(x => x.CorrelationId).MaximumLength(50);
    }
}
