using System.Text.RegularExpressions;

namespace EnglishApp.Shared.Common;

public enum RequestLogLevel { Information, Warning, Error }

/// <summary>
/// HTTP istek loglarının seviyesi (API ve Web aynı kuralı kullanır).
/// Yapay zeka istekleri doğası gereği yavaştır ve ücretsiz kota dolunca 503 döner: bunlar arıza değil, log ekranını kirletmesin.
/// </summary>
public static partial class RequestLogLevels
{
    /// <summary>Yapay zeka kullanan istekler için "yavaş" sınırı.</summary>
    public const double AiSlowMs = 10_000;

    public static RequestLogLevel Get(string? path, int statusCode, double elapsedMs, bool hasException, double normalSlowMs)
    {
        if (hasException) return RequestLogLevel.Error;
        var ai = IsAiPath(path);
        if (statusCode >= 500)
            // 503 = yapay zeka sağlayıcısı / kota şu an yanıt vermiyor (uygulama tarayıcı sesine vb. düşer)
            return ai && statusCode == 503 ? RequestLogLevel.Warning : RequestLogLevel.Error;
        return elapsedMs > (ai ? AiSlowMs : normalSlowMs) ? RequestLogLevel.Warning : RequestLogLevel.Information;
    }

    /// <summary>Yapay zeka (LLM, ses, ses tanıma) kullanan uçlar. Web'deki /bff/... yolları da eşleşir.</summary>
    public static bool IsAiPath(string? path) => !string.IsNullOrEmpty(path) && AiPathRegex().IsMatch(path);

    [GeneratedRegex(@"/(ai|reading|coach|listening|capture|travel-kits)(/|$)|/admin/content/(scenarios/generate|words/enrich)|/admin/settings/(test-ai|test-mail|sample-report)|/jobs/run",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AiPathRegex();
}
