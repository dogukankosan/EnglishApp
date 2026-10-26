using EnglishApp.Shared.Common;
using EnglishApp.Shared.Dtos;

namespace EnglishApp.Api.Infrastructure.Files;

/// <summary>
/// Content/audio altındaki hazır MP3'ler (Kokoro ile üretilmiş, Amerikan + İngiliz aksanı).
/// Dosya listesi bellekte tutulur, 5 dk'da bir yenilenir. Ses yoksa URL null döner, istemci tarayıcı sesini kullanır.
/// </summary>
public interface IAudioStore
{
    string? UrlFor(string voice, string text);
    string? WordUrl(string text, bool uk = false);
    void FillDialogue(IList<DialogueLineDto> lines);
    void FillLine(DialogueLineDto line, IReadOnlyList<string> speakersInOrder);
    string? GetPath(string fileName);
}

public sealed class AudioStore(IWebHostEnvironment env, ILogger<AudioStore> logger) : IAudioStore
{
    private readonly string _dir = Path.Combine(env.ContentRootPath, "Content", "audio");
    private readonly object _lock = new();
    private HashSet<string> _files = [];
    private DateTime _loadedUtc = DateTime.MinValue;

    public string? UrlFor(string voice, string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var name = AudioNaming.FileName(voice, text);
        return Files().Contains(name) ? "files/audio/" + name : null;
    }

    public string? WordUrl(string text, bool uk = false) => UrlFor(uk ? AudioNaming.WordVoiceUk : AudioNaming.WordVoice, text);

    /// <summary>Diyalog satırlarına, konuşmacı sırasına göre doğru sesin dosyasını bağlar (iki aksan).</summary>
    public void FillDialogue(IList<DialogueLineDto> lines)
    {
        var speakers = lines.Select(l => l.SpeakerRole).Where(s => s is not null).Cast<string>().Distinct().ToList();
        foreach (var l in lines) FillLine(l, speakers);
    }

    public void FillLine(DialogueLineDto line, IReadOnlyList<string> speakersInOrder)
    {
        line.AudioUrl = UrlFor(AudioNaming.VoiceForSpeaker(speakersInOrder, line.SpeakerRole), line.TextEn);
        line.AudioUrlUk = UrlFor(AudioNaming.VoiceForSpeaker(speakersInOrder, line.SpeakerRole, uk: true), line.TextEn);
    }

    public string? GetPath(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName) || fileName != Path.GetFileName(fileName) || !fileName.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase))
            return null;
        var path = Path.Combine(_dir, fileName);
        return File.Exists(path) ? path : null;
    }

    private HashSet<string> Files()
    {
        if (DateTime.UtcNow - _loadedUtc < TimeSpan.FromMinutes(5)) return _files;
        lock (_lock)
        {
            if (DateTime.UtcNow - _loadedUtc < TimeSpan.FromMinutes(5)) return _files;
            try
            {
                _files = Directory.Exists(_dir)
                    ? Directory.EnumerateFiles(_dir, "*.mp3").Select(Path.GetFileName).Cast<string>().ToHashSet(StringComparer.OrdinalIgnoreCase)
                    : [];
                logger.LogInformation("Hazır ses dosyaları yüklendi: {Count}", _files.Count);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Ses klasörü okunamadı: {Dir}", _dir);
            }
            _loadedUtc = DateTime.UtcNow;
            return _files;
        }
    }
}
