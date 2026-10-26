using System.Collections.ObjectModel;
using System.Data;
using System.Text.RegularExpressions;
using EnglishApp.Api.Infrastructure.Time;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Sinks.MSSqlServer;

namespace EnglishApp.Api.Infrastructure.Logging;

public static class LoggingSetup
{
    public const string SourceApp = "Api";

    /// <summary>
    /// Serilog: dosyaya her şey, SQL'e (app.Logs) Warning ve üstü.
    /// Zaman damgası her yerde Türkiye saati (TrTime özelliği).
    /// </summary>
    public static void Configure(LoggerConfiguration cfg, IConfiguration configuration, IHostEnvironment env)
    {
        var connectionString = configuration.GetConnectionString("Default");
        var logPath = configuration["Logging:FilePath"] ?? Path.Combine(env.ContentRootPath, "App_Data", "logs");

        cfg.MinimumLevel.Information()
           .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
           .MinimumLevel.Override("Microsoft.Hosting.Lifetime", LogEventLevel.Information)
           .MinimumLevel.Override("System", LogEventLevel.Warning)
           .Enrich.FromLogContext()
           .Enrich.With(new TrTimeEnricher())
           .Enrich.WithProperty("SourceApp", SourceApp)
           .Enrich.WithProperty("MachineName", Environment.MachineName)
           .WriteTo.Console(outputTemplate: "[{TrTime:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
           .WriteTo.File(
               Path.Combine(logPath, "api-.log"),
               rollingInterval: RollingInterval.Day,
               retainedFileCountLimit: 30,
               fileSizeLimitBytes: 50 * 1024 * 1024,
               rollOnFileSizeLimit: true,
               shared: true,
               outputTemplate: "{TrTime:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] [{CorrelationId}] [U:{UserId}] {RequestMethod} {RequestPath} {Message:lj}{NewLine}{Exception}");

        if (!string.IsNullOrWhiteSpace(connectionString))
        {
            cfg.WriteTo.MSSqlServer(
                connectionString,
                sinkOptions: new MSSqlServerSinkOptions
                {
                    SchemaName = "app",
                    TableName = "Logs",
                    AutoCreateSqlTable = false,
                    BatchPostingLimit = 50,
                    BatchPeriod = TimeSpan.FromSeconds(2)
                },
                columnOptions: BuildColumnOptions(),
                restrictedToMinimumLevel: LogEventLevel.Warning);
        }
    }

    public static ColumnOptions BuildColumnOptions()
    {
        var co = new ColumnOptions();
        co.Id.DataType = SqlDbType.BigInt;

        // Standart TimeStamp (sunucu saati) yerine kendi TR saatimizi yazıyoruz
        co.Store.Remove(StandardColumn.TimeStamp);
        // XML Properties yerine JSON (LogEvent) kolonunu "Properties" adıyla kullan
        co.Store.Remove(StandardColumn.Properties);
        co.Store.Add(StandardColumn.LogEvent);
        co.LogEvent.ColumnName = "Properties";
        co.LogEvent.ExcludeAdditionalProperties = true;
        co.LogEvent.ExcludeStandardColumns = true;
        co.Level.StoreAsEnum = false;

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
        return co;
    }
}

/// <summary>Her log kaydına Türkiye saatini "TrTime" olarak ekler.</summary>
public sealed class TrTimeEnricher : ILogEventEnricher
{
    private static readonly TrClock Clock = new();

    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        logEvent.AddPropertyIfAbsent(propertyFactory.CreateProperty("TrTime", Clock.ToTr(logEvent.Timestamp.UtcDateTime)));
    }
}

/// <summary>Loglara şifre/token gibi hassas veri yazılmasını engeller.</summary>
public static partial class LogSanitizer
{
    [GeneratedRegex(@"(?i)(password|token|secret|key|apikey)=[^&]*")]
    private static partial Regex SensitiveQueryRegex();

    public static string? SafeQuery(string? query) =>
        string.IsNullOrEmpty(query) ? query : SensitiveQueryRegex().Replace(query, "$1=***");

    public static string MaskEmail(string? email)
    {
        if (string.IsNullOrEmpty(email)) return "";
        var at = email.IndexOf('@');
        return at <= 1 ? "***" + email[Math.Max(at, 0)..] : email[0] + "***" + email[at..];
    }
}
