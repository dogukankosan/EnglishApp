using System.Security.Cryptography;
using System.Text;
using Dapper;
using EnglishApp.Api.Infrastructure.Data;
using EnglishApp.Shared.Dtos;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace EnglishApp.Api.Controllers;

/// <summary>
/// Web uygulamasının (BFF) uyarı/hata loglarını alır ve app.Logs tablosuna yazar.
/// Web veritabanına hiç bağlanmaz; sadece ortak gizli anahtarı (Bff:SharedKey) bilen Web log gönderebilir.
/// </summary>
[Route("api/v1/internal/logs")]
[AllowAnonymous]
[ApiExplorerSettings(IgnoreApi = true)]
public sealed class ClientLogsController(IDbConnectionFactory db, IConfiguration config, EnglishApp.Api.Infrastructure.Time.ITrClock clock) : ControllerBase
{
    private const int MaxBatch = 100;
    private static readonly HashSet<string> Levels = ["Warning", "Error", "Fatal"];

    [HttpPost]
    public async Task<IActionResult> Write([FromBody] List<ClientLogEntryDto>? entries, CancellationToken ct)
    {
        if (!HasValidBffKey()) return Unauthorized();
        if (entries is null || entries.Count == 0) return NoContent();

        var rows = entries.Take(MaxBatch)
            .Where(e => Levels.Contains(e.Level))
            .Select(e => new
            {
                TimeStamp = e.TimeStamp == default ? clock.Now : e.TimeStamp,
                e.Level,
                Message = Cut(e.Message, 8000),
                MessageTemplate = Cut(e.MessageTemplate, 4000),
                Exception = Cut(e.Exception, 32000),
                Properties = Cut(e.Properties, 16000),
                CorrelationId = Cut(e.CorrelationId, 50),
                e.UserId,
                RequestPath = Cut(e.RequestPath, 400),
                RequestMethod = Cut(e.RequestMethod, 10),
                ClientIp = Cut(e.ClientIp, 45),
                MachineName = Cut(e.MachineName, 100)
            }).ToList();
        if (rows.Count == 0) return NoContent();

        await using var c = await db.OpenAsync(ct);
        await c.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO app.Logs (TimeStamp, Level, SourceApp, Message, MessageTemplate, Exception, Properties,
                                  CorrelationId, UserId, RequestPath, RequestMethod, ClientIp, MachineName)
            VALUES (@TimeStamp, @Level, 'Web', @Message, @MessageTemplate, @Exception, @Properties,
                    @CorrelationId, @UserId, @RequestPath, @RequestMethod, @ClientIp, @MachineName)
            """, rows, cancellationToken: ct));
        return NoContent();
    }

    private bool HasValidBffKey()
    {
        var expected = config["Bff:SharedKey"];
        if (string.IsNullOrEmpty(expected) || !Request.Headers.TryGetValue("X-Bff-Key", out var key)) return false;
        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(key.ToString()), Encoding.UTF8.GetBytes(expected));
    }

    private static string? Cut(string? s, int max) => s is null || s.Length <= max ? s : s[..max];
}
