using System.Collections.ObjectModel;
using System.Data;
using Serilog;
using Serilog.Context;
using Serilog.Core;
using Serilog.Events;
using Serilog.Sinks.MSSqlServer;

namespace EnglishApp.Web.Infrastructure;

public static class TrTime
{
    private static readonly TimeZoneInfo Zone = Find();

    public static DateTime Now => FromUtc(DateTime.UtcNow);

    public static DateTime FromUtc(DateTime utc) =>
        DateTime.SpecifyKind(TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), Zone), DateTimeKind.Unspecified);

    /// <summary>API'den gelen Türkiye saatini UTC'ye çevirir (cookie süreleri için).</summary>
    public static DateTimeOffset ToUtcOffset(DateTime tr) =>
        new(TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(tr, DateTimeKind.Unspecified), Zone), TimeSpan.Zero);

    private static TimeZoneInfo Find()
    {
        foreach (var id in new[] { "Turkey Standard Time", "Europe/Istanbul" })
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); } catch (TimeZoneNotFoundException) { }
        }
        return TimeZoneInfo.CreateCustomTimeZone("TR+3", TimeSpan.FromHours(3), "Türkiye", "Türkiye");
    }
}

public static class WebLogging
{
    public static void Configure(LoggerConfiguration cfg, IConfiguration configuration, IHostEnvironment env)
    {
        var connectionString = configuration.GetConnectionString("Logs");
        var logPath = configuration["Logging:FilePath"] ?? Path.Combine(env.ContentRootPath, "App_Data", "logs");

        cfg.MinimumLevel.Information()
           .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
           .MinimumLevel.Override("Microsoft.Hosting.Lifetime", LogEventLevel.Information)
           .MinimumLevel.Override("System", LogEventLevel.Warning)
           // "Anahtar şifresiz saklanıyor" uyarısı her açılışta gelir; anahtarlar zaten App_Data'da (web'den erişilemez)
           .MinimumLevel.Override("Microsoft.AspNetCore.DataProtection", LogEventLevel.Error)
           // Yakalanmamış hatayı ErrorController correlation ID ile logluyor; aynı hata iki kez yazılmasın
           .MinimumLevel.Override("Microsoft.AspNetCore.Diagnostics.ExceptionHandlerMiddleware", LogEventLevel.Fatal)
           .Enrich.FromLogContext()
           .Enrich.With(new TrTimeEnricher())
           .Enrich.WithProperty("SourceApp", "Web")
           .Enrich.WithProperty("MachineName", Environment.MachineName)
           .WriteTo.Console(outputTemplate: "[{TrTime:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
           .WriteTo.File(
               Path.Combine(logPath, "web-.log"),
               rollingInterval: RollingInterval.Day,
               retainedFileCountLimit: 30,
               fileSizeLimitBytes: 50 * 1024 * 1024,
               rollOnFileSizeLimit: true,
               shared: true,
               outputTemplate: "{TrTime:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] [{CorrelationId}] [U:{UserId}] {RequestMethod} {RequestPath} {Message:lj}{NewLine}{Exception}");

        if (!string.IsNullOrWhiteSpace(connectionString))
        {
            var co = new ColumnOptions();
            co.Id.DataType = SqlDbType.BigInt;
            co.Store.Remove(StandardColumn.TimeStamp);
            co.Store.Remove(StandardColumn.Properties);
            co.Store.Add(StandardColumn.LogEvent);
            co.LogEvent.ColumnName = "Properties";
            co.LogEvent.ExcludeAdditionalProperties = true;
            co.LogEvent.ExcludeStandardColumns = true;
            co.AdditionalColumns = new Collection<SqlColumn>
            {
                new() { ColumnName = "TimeStamp", DataType = SqlDbType.DateTime2, PropertyName = "TrTime", AllowNull = false },
                new() { ColumnName = "SourceApp", DataType = SqlDbType.VarChar, DataLength = 10 },
                new() { ColumnName = "CorrelationId", DataType = SqlDbType.VarChar, DataLength = 50 },
                new() { ColumnName = "UserId", DataType = SqlDbType.Int },
                new() { ColumnName = "RequestPath", DataType = SqlDbType.NVarChar, DataLength = 400 },
                new() { ColumnName = "RequestMethod", DataType = SqlDbType.VarChar, DataLength = 10 },
                new() { ColumnName = "ClientIp", DataType = SqlDbType.VarChar, DataLength = 45 },
                new() { ColumnName = "MachineName", DataType = SqlDbType.NVarChar, DataLength = 100 }
            };

            cfg.WriteTo.MSSqlServer(connectionString,
                sinkOptions: new MSSqlServerSinkOptions { SchemaName = "app", TableName = "Logs", AutoCreateSqlTable = false },
                columnOptions: co,
                restrictedToMinimumLevel: LogEventLevel.Warning);
        }
    }
}

public sealed class TrTimeEnricher : ILogEventEnricher
{
    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory) =>
        logEvent.AddPropertyIfAbsent(propertyFactory.CreateProperty("TrTime", TrTime.FromUtc(logEvent.Timestamp.UtcDateTime)));
}

/// <summary>Her isteğe Correlation ID verir; API'ye de aynı ID gider (iki projedeki izi birleştirir).</summary>
public sealed class CorrelationIdMiddleware(RequestDelegate next)
{
    public const string HeaderName = "X-Correlation-Id";

    public async Task InvokeAsync(HttpContext context)
    {
        var correlationId = Guid.NewGuid().ToString("N")[..16];
        context.TraceIdentifier = correlationId;
        context.Response.Headers[HeaderName] = correlationId;

        var userId = context.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        using (LogContext.PushProperty("CorrelationId", correlationId))
        using (LogContext.PushProperty("RequestPath", context.Request.Path.Value))
        using (LogContext.PushProperty("RequestMethod", context.Request.Method))
        using (LogContext.PushProperty("ClientIp", context.Connection.RemoteIpAddress?.ToString()))
        using (LogContext.PushProperty("UserId", int.TryParse(userId, out var id) ? id : null))
        {
            await next(context);
        }
    }
}
