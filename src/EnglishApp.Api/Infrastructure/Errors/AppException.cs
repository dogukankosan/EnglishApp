namespace EnglishApp.Api.Infrastructure.Errors;

/// <summary>
/// İş kuralı hatası. Kullanıcıya mesajı gösterilir, Error seviyesinde loglanmaz.
/// </summary>
public class AppException : Exception
{
    public string Code { get; }
    public int StatusCode { get; }

    public AppException(string code, string message, int statusCode = StatusCodes.Status400BadRequest)
        : base(message)
    {
        Code = code;
        StatusCode = statusCode;
    }

    public static AppException NotFound(string message = "Kayıt bulunamadı.") =>
        new(ErrorCodes.NotFound, message, StatusCodes.Status404NotFound);

    public static AppException Forbidden(string message = "Bu işlem için yetkiniz yok.") =>
        new(ErrorCodes.Forbidden, message, StatusCodes.Status403Forbidden);

    public static AppException Conflict(string code, string message) =>
        new(code, message, StatusCodes.Status409Conflict);
}

public static class ErrorCodes
{
    public const string InternalError = "INTERNAL_ERROR";
    public const string ValidationError = "VALIDATION_ERROR";
    public const string NotFound = "NOT_FOUND";
    public const string Forbidden = "FORBIDDEN";
    public const string Unauthorized = "UNAUTHORIZED";
    public const string TooManyRequests = "TOO_MANY_REQUESTS";

    public const string InvalidCredentials = "INVALID_CREDENTIALS";
    public const string AccountLocked = "ACCOUNT_LOCKED";
    public const string AccountInactive = "ACCOUNT_INACTIVE";
    public const string InvalidRefreshToken = "INVALID_REFRESH_TOKEN";
    public const string PasswordChangeRequired = "PASSWORD_CHANGE_REQUIRED";
    public const string WrongPassword = "WRONG_PASSWORD";

    public const string EmailInUse = "EMAIL_IN_USE";
    public const string CannotModifySelf = "CANNOT_MODIFY_SELF";
    public const string ConcurrencyConflict = "CONCURRENCY_CONFLICT";
    public const string InvalidFile = "INVALID_FILE";
    public const string SettingNotFound = "SETTING_NOT_FOUND";
    public const string MailNotConfigured = "MAIL_NOT_CONFIGURED";
    public const string MailFailed = "MAIL_FAILED";
    public const string InvalidJobKey = "INVALID_JOB_KEY";
    public const string DailyLimitReached = "DAILY_LIMIT_REACHED";
    public const string CaptchaRequired = "CAPTCHA_REQUIRED";
    public const string CaptchaInvalid = "CAPTCHA_INVALID";
}
