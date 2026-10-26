using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dapper;
using EnglishApp.Api.Infrastructure;
using EnglishApp.Api.Infrastructure.Data;
using EnglishApp.Api.Infrastructure.Errors;
using EnglishApp.Api.Infrastructure.Security;
using EnglishApp.Api.Infrastructure.Time;
using EnglishApp.Shared.Dtos;
using FluentValidation;

namespace EnglishApp.Api.Services;

// ---------- Dizi / YouTube / podcast önerileri ----------

public interface IMediaService
{
    Task<IReadOnlyList<MediaItemDto>> ListAsync(CancellationToken ct);
    Task SetStatusAsync(int mediaId, byte status, CancellationToken ct);
}

public sealed class MediaService(IDbConnectionFactory db, ICurrentUser currentUser, ITrClock clock, ILogger<MediaService> logger) : IMediaService
{
    public async Task<IReadOnlyList<MediaItemDto>> ListAsync(CancellationToken ct)
    {
        await using var c = await db.OpenAsync(ct);
        return (await c.QueryAsync<MediaItemDto>(new CommandDefinition(
            """
            SELECT m.Id, m.MediaType, m.Title, m.Level, m.Accent, m.DescriptionTr, m.TipTr, m.Url, ISNULL(um.Status, 0) AS MyStatus
            FROM content.MediaRecommendations m
            LEFT JOIN study.UserMedia um ON um.MediaId = m.Id AND um.UserId = @userId
            WHERE m.IsActive = 1
            ORDER BY m.Level, m.SortOrder, m.Title
            """, new { userId = currentUser.Id }, cancellationToken: ct))).ToList();
    }

    /// <summary>0 = listeden çıkar, 1 Listemde, 2 İzliyorum, 3 Bitirdim.</summary>
    public async Task SetStatusAsync(int mediaId, byte status, CancellationToken ct)
    {
        if (status > 3) throw new AppException(ErrorCodes.ValidationError, "Geçersiz durum.");
        var userId = currentUser.Id;
        await using var c = await db.OpenAsync(ct);
        var exists = await c.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT CASE WHEN EXISTS (SELECT 1 FROM content.MediaRecommendations WHERE Id = @mediaId AND IsActive = 1) THEN 1 ELSE 0 END",
            new { mediaId }, cancellationToken: ct));
        if (!exists) throw AppException.NotFound("Öneri bulunamadı.");

        await c.ExecuteAsync(new CommandDefinition(status == 0
            ? "DELETE FROM study.UserMedia WHERE UserId = @userId AND MediaId = @mediaId"
            : """
              UPDATE study.UserMedia SET Status = @status, UpdatedAt = @now WHERE UserId = @userId AND MediaId = @mediaId;
              IF @@ROWCOUNT = 0 INSERT INTO study.UserMedia (UserId, MediaId, Status, UpdatedAt) VALUES (@userId, @mediaId, @status, @now);
              """, new { userId, mediaId, status, now = clock.Now }, cancellationToken: ct));
        logger.LogInformation("Öneri durumu: {MediaId} → {Status}", mediaId, status);
    }
}

// ---------- YouTube sahneleri ----------

public interface IVideoService
{
    Task<VideoDto> CreateAsync(VideoCreateRequest req, CancellationToken ct);
    Task<IReadOnlyList<VideoListItemDto>> ListAsync(CancellationToken ct);
    Task<VideoDto> GetAsync(long id, CancellationToken ct);
    Task DeleteAsync(long id, CancellationToken ct);
}

/// <summary>
/// YouTube videosu + kullanıcının yapıştırdığı zaman damgalı transkript.
/// Not: YouTube, altyazıları sunucudan otomatik çekmeyi engellediği için transkript elle yapıştırılır.
/// </summary>
public sealed partial class VideoService(IDbConnectionFactory db, ICurrentUser currentUser, ITrClock clock, ILogger<VideoService> logger) : IVideoService
{
    private const int MaxSegments = 600;

    private sealed class Row
    {
        public long Id { get; set; }
        public string YoutubeId { get; set; } = "";
        public string Title { get; set; } = "";
        public string SegmentsJson { get; set; } = "";
        public DateTime CreatedAt { get; set; }
    }

    public async Task<VideoDto> CreateAsync(VideoCreateRequest req, CancellationToken ct)
    {
        var youtubeId = ParseYoutubeId(req.Url) ?? throw new AppException(ErrorCodes.ValidationError, "Geçerli bir YouTube linki girin.");
        var segments = ParseTranscript(req.Transcript);
        if (segments.Count < 2)
            throw new AppException(ErrorCodes.ValidationError,
                "Transkriptte zaman damgası bulunamadı. YouTube'da '...Diğer > Transkripti göster' panelindeki metni zamanlarıyla birlikte kopyalayın.");

        var title = string.IsNullOrWhiteSpace(req.Title) ? $"YouTube {youtubeId}" : req.Title.Trim();
        await using var c = await db.OpenAsync(ct);
        var id = await c.ExecuteScalarAsync<long>(new CommandDefinition(
            """
            INSERT INTO study.Videos (UserId, YoutubeId, Title, SegmentsJson, CreatedAt)
            VALUES (@userId, @youtubeId, @title, @json, @now);
            SELECT CAST(SCOPE_IDENTITY() AS bigint);
            """,
            new
            {
                userId = currentUser.Id, youtubeId, title = title.Length > 200 ? title[..200] : title,
                json = JsonSerializer.Serialize(segments, JsonDefaults.Options), now = clock.Now
            }, cancellationToken: ct));

        logger.LogInformation("Video eklendi: {VideoId} {YoutubeId}, {Count} sahne", id, youtubeId, segments.Count);
        return await GetAsync(id, ct);
    }

    public async Task<IReadOnlyList<VideoListItemDto>> ListAsync(CancellationToken ct)
    {
        await using var c = await db.OpenAsync(ct);
        var rows = await c.QueryAsync<Row>(new CommandDefinition(
            "SELECT TOP (50) Id, YoutubeId, Title, SegmentsJson, CreatedAt FROM study.Videos WHERE UserId = @userId ORDER BY CreatedAt DESC",
            new { userId = currentUser.Id }, cancellationToken: ct));
        return rows.Select(r => new VideoListItemDto
        {
            Id = r.Id, YoutubeId = r.YoutubeId, Title = r.Title, CreatedAt = r.CreatedAt,
            SegmentCount = (JsonSerializer.Deserialize<List<VideoSegmentDto>>(r.SegmentsJson, JsonDefaults.Options) ?? []).Count
        }).ToList();
    }

    public async Task<VideoDto> GetAsync(long id, CancellationToken ct)
    {
        await using var c = await db.OpenAsync(ct);
        var r = await c.QuerySingleOrDefaultAsync<Row>(new CommandDefinition(
            "SELECT Id, YoutubeId, Title, SegmentsJson, CreatedAt FROM study.Videos WHERE Id = @id AND UserId = @userId",
            new { id, userId = currentUser.Id }, cancellationToken: ct)) ?? throw AppException.NotFound("Video bulunamadı.");
        return new VideoDto
        {
            Id = r.Id, YoutubeId = r.YoutubeId, Title = r.Title, CreatedAt = r.CreatedAt,
            Segments = JsonSerializer.Deserialize<List<VideoSegmentDto>>(r.SegmentsJson, JsonDefaults.Options) ?? []
        };
    }

    public async Task DeleteAsync(long id, CancellationToken ct)
    {
        await using var c = await db.OpenAsync(ct);
        var n = await c.ExecuteAsync(new CommandDefinition(
            "DELETE FROM study.Videos WHERE Id = @id AND UserId = @userId", new { id, userId = currentUser.Id }, cancellationToken: ct));
        if (n == 0) throw AppException.NotFound("Video bulunamadı.");
    }

    /// <summary>youtube.com/watch?v=, youtu.be/, /shorts/, /embed/, /live/ biçimlerini ve çıplak 11 karakterlik ID'yi tanır.</summary>
    public static string? ParseYoutubeId(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        url = url.Trim();
        if (IdRegex().IsMatch(url)) return url;
        var m = UrlRegex().Match(url);
        return m.Success ? m.Groups[1].Value : null;
    }

    /// <summary>
    /// "0:05 Hello there" / "0:05\nHello there" / "[01:02:03] text" / "00:00:05,120 --> ..." (SRT/VTT) biçimlerini sahnelere çevirir.
    /// </summary>
    public static List<VideoSegmentDto> ParseTranscript(string? text)
    {
        var result = new List<VideoSegmentDto>();
        if (string.IsNullOrWhiteSpace(text)) return result;

        VideoSegmentDto? current = null;
        foreach (var raw in text.Replace("\r", "").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line == "WEBVTT" || IsSrtIndex(line)) continue;

            var m = TimeRegex().Match(line);
            if (m.Success && TryParseTime(m.Groups[1].Value, out var start))
            {
                if (current is { Text.Length: > 0 }) result.Add(current);
                var rest = line[m.Length..];
                if (rest.Contains("-->")) rest = ""; // SRT/VTT bitiş zamanı satırı
                current = new VideoSegmentDto { Start = start, Text = Clean(rest) };
            }
            else if (current is not null)
            {
                current.Text = Clean((current.Text + " " + line).Trim());
            }
            if (result.Count >= MaxSegments) break;
        }
        if (current is { Text.Length: > 0 } && result.Count < MaxSegments) result.Add(current);

        // Aynı zamanlı / boş tekrarları ayıkla, sırala
        return result.Where(s => s.Text.Length > 0).GroupBy(s => s.Start).Select(g => g.First()).OrderBy(s => s.Start).ToList();
    }

    private static bool IsSrtIndex(string line) => line.Length <= 5 && line.All(char.IsDigit);

    private static bool TryParseTime(string s, out double seconds)
    {
        seconds = 0;
        var parts = s.Replace(',', '.').Split(':');
        double total = 0;
        foreach (var p in parts)
        {
            if (!double.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) return false;
            total = total * 60 + v;
        }
        seconds = Math.Round(total, 2);
        return parts.Length is 2 or 3;
    }

    private static string Clean(string s)
    {
        s = TagRegex().Replace(s, "").Replace("&amp;", "&").Replace("&#39;", "'").Replace("&quot;", "\"");
        s = SpaceRegex().Replace(s, " ").Trim();
        return s.Length > 400 ? s[..400] : s;
    }

    [GeneratedRegex(@"^[A-Za-z0-9_-]{11}$")]
    private static partial Regex IdRegex();

    [GeneratedRegex(@"(?:youtube\.com/(?:watch\?(?:.*&)?v=|shorts/|embed/|live/)|youtu\.be/)([A-Za-z0-9_-]{11})", RegexOptions.IgnoreCase)]
    private static partial Regex UrlRegex();

    [GeneratedRegex(@"^\[?((?:\d{1,2}:)?\d{1,2}:\d{2}(?:[.,]\d{1,3})?)\]?\s*")]
    private static partial Regex TimeRegex();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex TagRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex SpaceRegex();
}

public sealed class VideoCreateRequestValidator : AbstractValidator<VideoCreateRequest>
{
    public VideoCreateRequestValidator()
    {
        RuleFor(x => x.Url).NotEmpty().WithMessage("YouTube linki boş olamaz.").MaximumLength(300);
        RuleFor(x => x.Title).MaximumLength(200);
        RuleFor(x => x.Transcript).NotEmpty().WithMessage("Transkript boş olamaz.").MaximumLength(200_000).WithMessage("Transkript çok uzun.");
    }
}

public sealed class MediaStatusRequestValidator : AbstractValidator<MediaStatusRequest>
{
    public MediaStatusRequestValidator() => RuleFor(x => x.Status).InclusiveBetween((byte)0, (byte)3).WithMessage("Geçersiz durum.");
}
