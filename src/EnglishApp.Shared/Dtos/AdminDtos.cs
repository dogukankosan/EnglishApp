using EnglishApp.Shared.Enums;

namespace EnglishApp.Shared.Dtos;

public class AdminUserDto
{
    public int Id { get; set; }
    public string Email { get; set; } = "";
    public string FullName { get; set; } = "";
    public UserRole Role { get; set; }
    public string? ProfileImage { get; set; }
    public int? DailyTokenLimit { get; set; }
    public bool IsActive { get; set; }
    public bool MustChangePassword { get; set; }
    public bool IsLocked { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? LastLoginAt { get; set; }
    public int TodayTokens { get; set; }
}

public class CreateUserRequest
{
    public string Email { get; set; } = "";
    public string FullName { get; set; } = "";
    public UserRole Role { get; set; } = UserRole.User;
    /// <summary>Geçici şifre. Kullanıcı ilk girişte değiştirmek zorunda.</summary>
    public string Password { get; set; } = "";
    public int? DailyTokenLimit { get; set; }
}

public class UpdateUserRequest
{
    public string Email { get; set; } = "";
    public string FullName { get; set; } = "";
    public UserRole Role { get; set; }
    public int? DailyTokenLimit { get; set; }
}

public class ResetPasswordRequest
{
    public string NewPassword { get; set; } = "";
}

public class SettingDto
{
    public string Key { get; set; } = "";
    /// <summary>Şifreli ayarlarda değer hiçbir zaman dönmez.</summary>
    public string? Value { get; set; }
    public bool IsEncrypted { get; set; }
    public bool HasValue { get; set; }
    public string GroupName { get; set; } = "";
    public string? DescriptionTr { get; set; }
    public DateTime? UpdatedAt { get; set; }
}

public class UpdateSettingRequest
{
    public string Key { get; set; } = "";
    public string? Value { get; set; }
}

public class SaveSettingsRequest
{
    public List<UpdateSettingRequest> Items { get; set; } = [];
    /// <summary>
    /// SMTP ayarı değiştiyse kaydetmeden önce bu adrese test maili gider (boşsa işlemi yapan adminin adresi).
    /// Test maili başarısızsa hiçbir ayar kaydedilmez.
    /// </summary>
    public string? TestMailTo { get; set; }
}

public class SaveSettingsResultDto
{
    public int SavedCount { get; set; }
    public bool TestMailSent { get; set; }
    public string? TestMailTo { get; set; }
}

public class TestMailRequest
{
    public string ToAddress { get; set; } = "";
}

public class LogQuery
{
    public string? Level { get; set; }
    public string? SourceApp { get; set; }
    public DateTime? From { get; set; }
    public DateTime? To { get; set; }
    public int? UserId { get; set; }
    public string? CorrelationId { get; set; }
    public string? Search { get; set; }
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 50;
}

public class LogItemDto
{
    public long Id { get; set; }
    public DateTime TimeStamp { get; set; }
    public string Level { get; set; } = "";
    public string? SourceApp { get; set; }
    public string? Message { get; set; }
    public string? Exception { get; set; }
    public string? Properties { get; set; }
    public string? CorrelationId { get; set; }
    public int? UserId { get; set; }
    public string? RequestPath { get; set; }
    public string? RequestMethod { get; set; }
    public string? ClientIp { get; set; }
}
