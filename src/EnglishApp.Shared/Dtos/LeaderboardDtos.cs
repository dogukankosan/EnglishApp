namespace EnglishApp.Shared.Dtos;

/// <summary>Sıralama tablosu: tüm aktif kullanıcıların genel durumu (e-posta ve kullanıcı numarası verilmez).</summary>
public class LeaderboardDto
{
    /// <summary>today | week | month</summary>
    public string Period { get; set; } = "week";
    public string PeriodLabel { get; set; } = "";
    public DateTime From { get; set; }
    public DateTime To { get; set; }
    public List<LeaderboardRowDto> Items { get; set; } = [];
}

public class LeaderboardRowDto
{
    public int Rank { get; set; }
    public string FullName { get; set; } = "";
    public string? ProfileImage { get; set; }
    public bool IsMe { get; set; }
    public bool IsAdmin { get; set; }

    // Genel durum
    public string EstimatedLevel { get; set; } = "";
    public string? TargetLevel { get; set; }
    public int Vocabulary { get; set; }
    public int Streak { get; set; }
    public bool StudiedToday { get; set; }
    public DateTime? LastStudyDate { get; set; }

    // Seçilen dönem
    public int Reviews { get; set; }
    public int NewWords { get; set; }
    public int Minutes { get; set; }
    public int ActiveDays { get; set; }
    public int? AccuracyPercent { get; set; }
    /// <summary>Sıralama puanı: çalışılan kart + 2 × yeni kelime.</summary>
    public int Points { get; set; }
}
