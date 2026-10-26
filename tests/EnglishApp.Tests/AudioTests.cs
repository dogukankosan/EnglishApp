using System.Text.Json.Nodes;
using EnglishApp.Api.Services.Ai;
using EnglishApp.Shared.Common;

namespace EnglishApp.Tests;

public class AudioTests
{
    /// <summary>
    /// tools/audiogen/generate_audio.py aynı (ses, metin) için aynı adı üretmeli.
    /// Bu değer Python'dan alındı: kural tek tarafta değişirse bu test kırılır.
    /// </summary>
    [Fact]
    public void FileName_matches_python_generator() =>
        Assert.Equal("557fe6395a4ca7345084.mp3", AudioNaming.FileName("am_michael", "Sure. See you around!"));

    [Fact]
    public void FileName_ignores_surrounding_whitespace() =>
        Assert.Equal(AudioNaming.FileName("af_heart", "Hello"), AudioNaming.FileName("af_heart", "  Hello "));

    [Fact]
    public void Speakers_get_alternating_voices_by_first_appearance()
    {
        string[] speakers = ["Agent", "Passenger"];
        Assert.Equal("af_heart", AudioNaming.VoiceForSpeaker(speakers, "Agent"));
        Assert.Equal("am_michael", AudioNaming.VoiceForSpeaker(speakers, "Passenger"));
        Assert.Equal("af_heart", AudioNaming.VoiceForSpeaker(speakers, "Unknown"));
    }

    [Fact]
    public void Raw_pcm_gets_wav_header_and_wav_is_left_untouched()
    {
        var pcm = new byte[4800];
        var wav = GeminiTtsService.EnsureWav(pcm);
        Assert.Equal(44 + pcm.Length, wav.Length);
        Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(wav, 0, 4));
        Assert.Same(wav, GeminiTtsService.EnsureWav(wav));
    }

    [Fact]
    public void Audio_is_found_in_both_response_formats()
    {
        var b64 = Convert.ToBase64String(new byte[200]);
        var interactions = JsonNode.Parse($$$"""{"steps":[{"content":[{"type":"audio","data":"{{{b64}}}"}]}]}""");
        var generate = JsonNode.Parse($$$"""{"candidates":[{"content":{"parts":[{"inlineData":{"mimeType":"audio/L16","data":"{{{b64}}}"}}]}}]}""");
        Assert.Equal(b64, GeminiTtsService.FindAudioBase64(interactions));
        Assert.Equal(b64, GeminiTtsService.FindAudioBase64(generate));
        Assert.Null(GeminiTtsService.FindAudioBase64(JsonNode.Parse("""{"error":{"message":"quota"}}""")));
    }
}
