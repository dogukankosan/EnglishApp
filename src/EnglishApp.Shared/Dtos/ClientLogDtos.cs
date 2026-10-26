namespace EnglishApp.Shared.Dtos;

/// <summary>Web (BFF) uygulamasının kendi uyarı/hata logları; API bunları app.Logs tablosuna yazar.</summary>
public class ClientLogEntryDto
{
    /// <summary>Türkiye saati.</summary>
    public DateTime TimeStamp { get; set; }
    /// <summary>Warning | Error | Fatal</summary>
    public string Level { get; set; } = "";
    public string? Message { get; set; }
    public string? MessageTemplate { get; set; }
    public string? Exception { get; set; }
    /// <summary>JSON</summary>
    public string? Properties { get; set; }
    public string? CorrelationId { get; set; }
    public int? UserId { get; set; }
    public string? RequestPath { get; set; }
    public string? RequestMethod { get; set; }
    public string? ClientIp { get; set; }
    public string? MachineName { get; set; }
}
