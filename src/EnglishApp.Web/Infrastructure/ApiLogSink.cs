using System.Net.Http.Json;
using System.Text.Json;
using EnglishApp.Shared.Dtos;
using Serilog.Core;
using Serilog.Debugging;
using Serilog.Events;

namespace EnglishApp.Web.Infrastructure;

/// <summary>
/// Web'in uyarı/hata loglarını toplu halde API'ye gönderir (API app.Logs'a yazar).
/// Web veritabanına bağlanmaz. Gönderilemeyen kayıtlar Serilog tarafından birkaç kez yeniden denenir;
/// dosya logu her durumda yazılmaya devam eder.
/// </summary>
public sealed class ApiLogSink : IBatchedLogEventSink, IDisposable
{
    private static readonly HashSet<string> ColumnProps =
        ["TrTime", "SourceApp", "CorrelationId", "UserId", "RequestPath", "RequestMethod", "ClientIp", "MachineName"];

    private readonly HttpClient _http;
    private readonly string _bffKey;

    public ApiLogSink(string apiBaseUrl, string bffKey)
    {
        // Kendi HttpClient'ı: IHttpClientFactory logları burada tekrar log üretmesin (döngü olmasın)
        _http = new HttpClient { BaseAddress = new Uri(apiBaseUrl.TrimEnd('/') + "/"), Timeout = TimeSpan.FromSeconds(15) };
        _bffKey = bffKey;
    }

    public async Task EmitBatchAsync(IReadOnlyCollection<LogEvent> batch)
    {
        var entries = batch.Select(Map).ToList();
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/v1/internal/logs") { Content = JsonContent.Create(entries) };
        request.Headers.Add("X-Bff-Key", _bffKey);
        using var response = await _http.SendAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            // Yeniden denensin diye hata fırlatılır; Serilog'un kendi tanı loguna düşer
            SelfLog.WriteLine("Web logları API'ye gönderilemedi: {0}", (int)response.StatusCode);
            response.EnsureSuccessStatusCode();
        }
    }

    public Task OnEmptyBatchAsync() => Task.CompletedTask;

    public static ClientLogEntryDto Map(LogEvent e)
    {
        string? Str(string name) => e.Properties.TryGetValue(name, out var v) && v is ScalarValue { Value: { } o } ? o.ToString() : null;

        var extra = e.Properties.Where(p => !ColumnProps.Contains(p.Key))
            .ToDictionary(p => p.Key, p => p.Value is ScalarValue s ? s.Value?.ToString() : p.Value.ToString());

        return new ClientLogEntryDto
        {
            TimeStamp = e.Properties.TryGetValue("TrTime", out var t) && t is ScalarValue { Value: DateTime tr } ? tr : TrTime.FromUtc(e.Timestamp.UtcDateTime),
            Level = e.Level.ToString(),
            Message = e.RenderMessage(),
            MessageTemplate = e.MessageTemplate.Text,
            Exception = e.Exception?.ToString(),
            Properties = extra.Count == 0 ? null : JsonSerializer.Serialize(extra),
            CorrelationId = Str("CorrelationId"),
            UserId = int.TryParse(Str("UserId"), out var uid) ? uid : null,
            RequestPath = Str("RequestPath"),
            RequestMethod = Str("RequestMethod"),
            ClientIp = Str("ClientIp"),
            MachineName = Str("MachineName")
        };
    }

    public void Dispose() => _http.Dispose();
}
