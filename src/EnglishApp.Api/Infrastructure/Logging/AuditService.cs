using System.Text.Json;
using Dapper;
using EnglishApp.Api.Infrastructure.Data;
using EnglishApp.Api.Infrastructure.Security;
using EnglishApp.Api.Infrastructure.Time;

namespace EnglishApp.Api.Infrastructure.Logging;

/// <summary>Admin işlemlerini app.AuditLogs'a yazar (kim, neyi, eski/yeni değer).</summary>
public interface IAuditService
{
    Task WriteAsync(string action, string entityType, object? entityId, object? oldValue = null, object? newValue = null);
}

public sealed class AuditService(
    IDbConnectionFactory db,
    ICurrentUser currentUser,
    ITrClock clock,
    ILogger<AuditService> logger) : IAuditService
{
    public async Task WriteAsync(string action, string entityType, object? entityId, object? oldValue = null, object? newValue = null)
    {
        try
        {
            await using var conn = await db.OpenAsync();
            await conn.ExecuteAsync(
                """
                INSERT INTO app.AuditLogs (ActorUserId, Action, EntityType, EntityId, OldValue, NewValue, ClientIp, CorrelationId, CreatedAt)
                VALUES (@Actor, @Action, @EntityType, @EntityId, @OldValue, @NewValue, @Ip, @CorrelationId, @Now)
                """,
                new
                {
                    Actor = currentUser.IsAuthenticated ? currentUser.Id : (int?)null,
                    Action = action,
                    EntityType = entityType,
                    EntityId = entityId?.ToString(),
                    OldValue = oldValue is null ? null : JsonSerializer.Serialize(oldValue, JsonDefaults.Options),
                    NewValue = newValue is null ? null : JsonSerializer.Serialize(newValue, JsonDefaults.Options),
                    Ip = currentUser.ClientIp,
                    currentUser.CorrelationId,
                    Now = clock.Now
                });

            logger.LogInformation("Audit: {AuditAction} {EntityType}#{EntityId}", action, entityType, entityId);
        }
        catch (Exception ex)
        {
            // Audit yazılamadı diye asıl işlem bozulmasın, ama mutlaka loglansın
            logger.LogError(ex, "Audit kaydı yazılamadı: {AuditAction} {EntityType}#{EntityId}", action, entityType, entityId);
        }
    }
}
