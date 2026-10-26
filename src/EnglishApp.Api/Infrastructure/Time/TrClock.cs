namespace EnglishApp.Api.Infrastructure.Time;

/// <summary>
/// Uygulamadaki TEK saat kaynağı. Sunucu yabancı olduğu için
/// DateTime.Now / DateTime.UtcNow doğrudan kullanılmaz, her zaman Türkiye saati döner.
/// </summary>
public interface ITrClock
{
    DateTime Now { get; }
    DateTime Today { get; }
    DateTime ToTr(DateTime utc);
}

public sealed class TrClock : ITrClock
{
    private static readonly TimeZoneInfo TrZone = FindZone();

    public DateTime Now => DateTime.SpecifyKind(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, TrZone), DateTimeKind.Unspecified);
    public DateTime Today => Now.Date;

    public DateTime ToTr(DateTime utc) =>
        DateTime.SpecifyKind(TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), TrZone), DateTimeKind.Unspecified);

    public static DateTime NowStatic => DateTime.SpecifyKind(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, TrZone), DateTimeKind.Unspecified);

    private static TimeZoneInfo FindZone()
    {
        foreach (var id in new[] { "Turkey Standard Time", "Europe/Istanbul" })
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
            catch (TimeZoneNotFoundException) { }
        }
        // Türkiye 2016'dan beri sabit UTC+3
        return TimeZoneInfo.CreateCustomTimeZone("TR+3", TimeSpan.FromHours(3), "Türkiye", "Türkiye");
    }
}
