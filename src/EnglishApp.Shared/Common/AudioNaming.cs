using System.Security.Cryptography;
using System.Text;

namespace EnglishApp.Shared.Common;

/// <summary>
/// Kokoro ile önceden üretilen MP3'lerin isimlendirmesi.
/// Dosya adı (ses + metin)'den hesaplanır: DB'de yol tutmaya gerek kalmaz, lokal ve canlı DB'nin Id'leri farklı olsa da çalışır.
/// tools/audiogen/generate_audio.py AYNI kuralı kullanır, değiştirirsen ikisini birlikte değiştir.
/// </summary>
public static class AudioNaming
{
    // ----- Amerikan aksanı -----
    /// <summary>Kelime ve örnek cümleler için ses (Kokoro: Amerikan kadın).</summary>
    public const string WordVoice = "af_heart";
    /// <summary>Diyaloglarda konuşmacılara sırayla verilen sesler (1. konuşmacı kadın, 2. erkek).</summary>
    public static readonly string[] DialogueVoices = ["af_heart", "am_michael"];

    // ----- İngiliz aksanı -----
    public const string WordVoiceUk = "bf_emma";
    public static readonly string[] DialogueVoicesUk = ["bf_emma", "bm_george"];

    public static string FileName(string voice, string text)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(voice + "|" + text.Trim()));
        return Convert.ToHexString(hash).ToLowerInvariant()[..20] + ".mp3";
    }

    /// <summary>Konuşmacının diyalogdaki ilk görünme sırasına göre ses.</summary>
    public static string VoiceForSpeaker(IReadOnlyList<string> speakersInOrder, string? speaker, bool uk = false)
    {
        var voices = uk ? DialogueVoicesUk : DialogueVoices;
        var index = speaker is null ? 0 : Math.Max(0, IndexOf(speakersInOrder, speaker));
        return voices[index % voices.Length];
    }

    private static int IndexOf(IReadOnlyList<string> list, string value)
    {
        for (var i = 0; i < list.Count; i++)
            if (string.Equals(list[i], value, StringComparison.Ordinal)) return i;
        return -1;
    }
}
