namespace EnglishApp.Shared.Dtos;

public class ProfileDto
{
    public int Id { get; set; }
    public string Email { get; set; } = "";
    public string FullName { get; set; } = "";
    public string Role { get; set; } = "";
    public string? ProfileImage { get; set; }
    public int? DailyTokenLimit { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? LastLoginAt { get; set; }
    public UserSettingsDto Settings { get; set; } = new();
}

public class UpdateProfileRequest
{
    public string FullName { get; set; } = "";
}

public class UserSettingsDto
{
    public short DailyNewWordLimit { get; set; }
    public short DailyReviewLimit { get; set; }
    public decimal DesiredRetention { get; set; }
    public bool ReminderEnabled { get; set; }
    /// <summary>"HH:mm"</summary>
    public string ReminderTime { get; set; } = "20:00";
    public bool WeeklyReportEnabled { get; set; }
    public byte WeeklyReportDay { get; set; }
    public string? TtsVoice { get; set; }
    public decimal TtsRate { get; set; }
    public bool PlacementTestDone { get; set; }
    public string? TargetLevel { get; set; }
    public string? TargetDate { get; set; }
    public bool StreakFreeze { get; set; } = true;
}

public class UpdateSettingsRequest
{
    public short DailyNewWordLimit { get; set; }
    public short DailyReviewLimit { get; set; }
    public decimal DesiredRetention { get; set; }
    public bool ReminderEnabled { get; set; }
    public string ReminderTime { get; set; } = "20:00";
    public bool WeeklyReportEnabled { get; set; }
    public byte WeeklyReportDay { get; set; }
    public string? TtsVoice { get; set; }
    public decimal TtsRate { get; set; }
}
