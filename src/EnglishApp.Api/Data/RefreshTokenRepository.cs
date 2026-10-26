using Dapper;
using EnglishApp.Api.Infrastructure.Data;
using EnglishApp.Shared.Enums;

namespace EnglishApp.Api.Data;

public sealed class RefreshTokenRow
{
    public long Id { get; set; }
    public int UserId { get; set; }
    public Guid FamilyId { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime? RevokedAt { get; set; }
    public RevokeReason? RevokeReason { get; set; }
}

public interface IRefreshTokenRepository
{
    Task<long> InsertAsync(int userId, byte[] hash, Guid familyId, DateTime expiresAt, DateTime now, string? ip, string? userAgent, CancellationToken ct = default);
    Task<RefreshTokenRow?> GetByHashAsync(byte[] hash, CancellationToken ct = default);
    Task<bool> RotateAsync(long oldId, long newId, DateTime now, CancellationToken ct = default);
    Task RevokeAsync(long id, RevokeReason reason, DateTime now, CancellationToken ct = default);
    Task RevokeFamilyAsync(Guid familyId, RevokeReason reason, DateTime now, CancellationToken ct = default);
    Task RevokeAllForUserAsync(int userId, RevokeReason reason, DateTime now, CancellationToken ct = default);
    Task<int> DeleteExpiredAsync(DateTime olderThan, CancellationToken ct = default);
}

public sealed class RefreshTokenRepository(IDbConnectionFactory db) : IRefreshTokenRepository
{
    public async Task<long> InsertAsync(int userId, byte[] hash, Guid familyId, DateTime expiresAt, DateTime now, string? ip, string? userAgent, CancellationToken ct = default)
    {
        await using var c = await db.OpenAsync(ct);
        return await c.ExecuteScalarAsync<long>(new CommandDefinition(
            """
            INSERT INTO auth.RefreshTokens (UserId, TokenHash, FamilyId, ExpiresAt, CreatedAt, CreatedByIp, UserAgent)
            VALUES (@userId, @hash, @familyId, @expiresAt, @now, @ip, @userAgent);
            SELECT CAST(SCOPE_IDENTITY() AS bigint);
            """, new { userId, hash, familyId, expiresAt, now, ip, userAgent }, cancellationToken: ct));
    }

    public async Task<RefreshTokenRow?> GetByHashAsync(byte[] hash, CancellationToken ct = default)
    {
        await using var c = await db.OpenAsync(ct);
        return await c.QuerySingleOrDefaultAsync<RefreshTokenRow>(new CommandDefinition(
            "SELECT Id, UserId, FamilyId, ExpiresAt, RevokedAt, RevokeReason FROM auth.RefreshTokens WHERE TokenHash = @hash",
            new { hash }, cancellationToken: ct));
    }

    /// <summary>Eski token'ı sadece hâlâ geçerliyse iptal eder (aynı anda iki yenileme isteğine karşı).</summary>
    public async Task<bool> RotateAsync(long oldId, long newId, DateTime now, CancellationToken ct = default)
    {
        await using var c = await db.OpenAsync(ct);
        var affected = await c.ExecuteAsync(new CommandDefinition(
            """
            UPDATE auth.RefreshTokens SET RevokedAt = @now, RevokeReason = 1, ReplacedByTokenId = @newId
            WHERE Id = @oldId AND RevokedAt IS NULL
            """, new { oldId, newId, now }, cancellationToken: ct));
        return affected == 1;
    }

    public async Task RevokeAsync(long id, RevokeReason reason, DateTime now, CancellationToken ct = default)
    {
        await using var c = await db.OpenAsync(ct);
        await c.ExecuteAsync(new CommandDefinition(
            "UPDATE auth.RefreshTokens SET RevokedAt = @now, RevokeReason = @reason WHERE Id = @id AND RevokedAt IS NULL",
            new { id, reason = (byte)reason, now }, cancellationToken: ct));
    }

    public async Task RevokeFamilyAsync(Guid familyId, RevokeReason reason, DateTime now, CancellationToken ct = default)
    {
        await using var c = await db.OpenAsync(ct);
        await c.ExecuteAsync(new CommandDefinition(
            "UPDATE auth.RefreshTokens SET RevokedAt = @now, RevokeReason = @reason WHERE FamilyId = @familyId AND RevokedAt IS NULL",
            new { familyId, reason = (byte)reason, now }, cancellationToken: ct));
    }

    public async Task RevokeAllForUserAsync(int userId, RevokeReason reason, DateTime now, CancellationToken ct = default)
    {
        await using var c = await db.OpenAsync(ct);
        await c.ExecuteAsync(new CommandDefinition(
            "UPDATE auth.RefreshTokens SET RevokedAt = @now, RevokeReason = @reason WHERE UserId = @userId AND RevokedAt IS NULL",
            new { userId, reason = (byte)reason, now }, cancellationToken: ct));
    }

    public async Task<int> DeleteExpiredAsync(DateTime olderThan, CancellationToken ct = default)
    {
        await using var c = await db.OpenAsync(ct);
        return await c.ExecuteAsync(new CommandDefinition(
            "DELETE TOP (5000) FROM auth.RefreshTokens WHERE ExpiresAt < @olderThan", new { olderThan }, cancellationToken: ct));
    }
}
