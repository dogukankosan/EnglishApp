using Dapper;
using EnglishApp.Api.Infrastructure.Data;
using EnglishApp.Shared.Dtos;
using EnglishApp.Shared.Enums;

namespace EnglishApp.Api.Data;

public sealed class UserRow
{
    public int Id { get; set; }
    public string Email { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public string FullName { get; set; } = "";
    public string? ProfileImagePath { get; set; }
    public UserRole RoleId { get; set; }
    public int? DailyTokenLimit { get; set; }
    public bool IsActive { get; set; }
    public bool MustChangePassword { get; set; }
    public Guid SecurityStamp { get; set; }
    public byte FailedLoginCount { get; set; }
    public DateTime? LockoutEndAt { get; set; }
    public DateTime? LastLoginAt { get; set; }
    public DateTime CreatedAt { get; set; }
}

public sealed class UserAuthState
{
    public bool IsActive { get; set; }
    public Guid SecurityStamp { get; set; }
}

public interface IUserRepository
{
    Task<UserRow?> GetByIdAsync(int id, CancellationToken ct = default);
    Task<UserRow?> GetByEmailAsync(string email, CancellationToken ct = default);
    Task<UserAuthState?> GetAuthStateAsync(int id, CancellationToken ct = default);
    Task<bool> EmailExistsAsync(string email, int? excludeId, CancellationToken ct = default);
    Task<bool> AnyActiveAdminAsync(CancellationToken ct = default);
    Task<int> CreateAsync(UserRow user, int? createdBy, DateTime now, CancellationToken ct = default);
    Task RecordLoginSuccessAsync(int id, DateTime now, string? newHash, CancellationToken ct = default);
    Task RecordLoginFailureAsync(int id, byte failedCount, DateTime? lockoutEnd, CancellationToken ct = default);
    Task UpdatePasswordAsync(int id, string hash, bool mustChange, int updatedBy, DateTime now, CancellationToken ct = default);
    Task UpdateFullNameAsync(int id, string fullName, DateTime now, CancellationToken ct = default);
    Task UpdatePhotoAsync(int id, string? path, DateTime now, CancellationToken ct = default);
    Task<IReadOnlyList<AdminUserDto>> ListForAdminAsync(DateTime today, DateTime now, CancellationToken ct = default);
    Task UpdateByAdminAsync(int id, UpdateUserRequest req, int updatedBy, DateTime now, CancellationToken ct = default);
    Task SetActiveAsync(int id, bool isActive, int updatedBy, DateTime now, CancellationToken ct = default);
    Task SoftDeleteAsync(int id, int deletedBy, DateTime now, CancellationToken ct = default);
    Task UnlockAsync(int id, CancellationToken ct = default);
}

public sealed class UserRepository(IDbConnectionFactory db) : IUserRepository
{
    private const string SelectColumns =
        """
        SELECT Id, Email, PasswordHash, FullName, ProfileImagePath, RoleId, DailyTokenLimit, IsActive,
               MustChangePassword, SecurityStamp, FailedLoginCount, LockoutEndAt, LastLoginAt, CreatedAt
        FROM auth.Users
        """;

    public async Task<UserRow?> GetByIdAsync(int id, CancellationToken ct = default)
    {
        await using var c = await db.OpenAsync(ct);
        return await c.QuerySingleOrDefaultAsync<UserRow>(new CommandDefinition(
            SelectColumns + " WHERE Id = @id AND IsDeleted = 0", new { id }, cancellationToken: ct));
    }

    public async Task<UserRow?> GetByEmailAsync(string email, CancellationToken ct = default)
    {
        await using var c = await db.OpenAsync(ct);
        return await c.QuerySingleOrDefaultAsync<UserRow>(new CommandDefinition(
            SelectColumns + " WHERE Email = @email AND IsDeleted = 0",
            new { email = new DbString { Value = email, IsAnsi = true, Length = 256 } }, cancellationToken: ct));
    }

    public async Task<UserAuthState?> GetAuthStateAsync(int id, CancellationToken ct = default)
    {
        await using var c = await db.OpenAsync(ct);
        return await c.QuerySingleOrDefaultAsync<UserAuthState>(new CommandDefinition(
            "SELECT IsActive, SecurityStamp FROM auth.Users WHERE Id = @id AND IsDeleted = 0", new { id }, cancellationToken: ct));
    }

    public async Task<bool> EmailExistsAsync(string email, int? excludeId, CancellationToken ct = default)
    {
        await using var c = await db.OpenAsync(ct);
        return await c.ExecuteScalarAsync<bool>(new CommandDefinition(
            """
            SELECT CASE WHEN EXISTS (SELECT 1 FROM auth.Users
                WHERE Email = @email AND IsDeleted = 0 AND (@excludeId IS NULL OR Id <> @excludeId)) THEN 1 ELSE 0 END
            """,
            new { email = new DbString { Value = email, IsAnsi = true, Length = 256 }, excludeId }, cancellationToken: ct));
    }

    public async Task<bool> AnyActiveAdminAsync(CancellationToken ct = default)
    {
        await using var c = await db.OpenAsync(ct);
        return await c.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT CASE WHEN EXISTS (SELECT 1 FROM auth.Users WHERE RoleId = 1 AND IsDeleted = 0 AND IsActive = 1) THEN 1 ELSE 0 END",
            cancellationToken: ct));
    }

    public async Task<int> CreateAsync(UserRow user, int? createdBy, DateTime now, CancellationToken ct = default)
    {
        return await db.InTransactionAsync(async (c, tx) =>
        {
            var id = await c.ExecuteScalarAsync<int>(new CommandDefinition(
                """
                INSERT INTO auth.Users (Email, PasswordHash, FullName, RoleId, DailyTokenLimit, IsActive, MustChangePassword, CreatedAt, CreatedBy)
                VALUES (@Email, @PasswordHash, @FullName, @RoleId, @DailyTokenLimit, 1, @MustChangePassword, @Now, @CreatedBy);
                SELECT CAST(SCOPE_IDENTITY() AS int);
                """,
                new
                {
                    Email = new DbString { Value = user.Email, IsAnsi = true, Length = 256 },
                    user.PasswordHash, user.FullName, RoleId = (byte)user.RoleId, user.DailyTokenLimit,
                    user.MustChangePassword, Now = now, CreatedBy = createdBy
                }, tx, cancellationToken: ct));

            await c.ExecuteAsync(new CommandDefinition(
                "INSERT INTO study.UserSettings (UserId) VALUES (@id)", new { id }, tx, cancellationToken: ct));
            return id;
        }, ct);
    }

    public async Task RecordLoginSuccessAsync(int id, DateTime now, string? newHash, CancellationToken ct = default)
    {
        await using var c = await db.OpenAsync(ct);
        await c.ExecuteAsync(new CommandDefinition(
            """
            UPDATE auth.Users
            SET FailedLoginCount = 0, LockoutEndAt = NULL, LastLoginAt = @now,
                PasswordHash = COALESCE(@newHash, PasswordHash)
            WHERE Id = @id
            """, new { id, now, newHash }, cancellationToken: ct));
    }

    public async Task RecordLoginFailureAsync(int id, byte failedCount, DateTime? lockoutEnd, CancellationToken ct = default)
    {
        await using var c = await db.OpenAsync(ct);
        await c.ExecuteAsync(new CommandDefinition(
            "UPDATE auth.Users SET FailedLoginCount = @failedCount, LockoutEndAt = @lockoutEnd WHERE Id = @id",
            new { id, failedCount, lockoutEnd }, cancellationToken: ct));
    }

    public async Task UpdatePasswordAsync(int id, string hash, bool mustChange, int updatedBy, DateTime now, CancellationToken ct = default)
    {
        await using var c = await db.OpenAsync(ct);
        // SecurityStamp yenilenir → eski tüm access token'lar geçersiz olur
        await c.ExecuteAsync(new CommandDefinition(
            """
            UPDATE auth.Users
            SET PasswordHash = @hash, MustChangePassword = @mustChange, SecurityStamp = NEWID(),
                FailedLoginCount = 0, LockoutEndAt = NULL, UpdatedAt = @now, UpdatedBy = @updatedBy
            WHERE Id = @id AND IsDeleted = 0
            """, new { id, hash, mustChange, updatedBy, now }, cancellationToken: ct));
    }

    public async Task UpdateFullNameAsync(int id, string fullName, DateTime now, CancellationToken ct = default)
    {
        await using var c = await db.OpenAsync(ct);
        await c.ExecuteAsync(new CommandDefinition(
            "UPDATE auth.Users SET FullName = @fullName, UpdatedAt = @now, UpdatedBy = @id WHERE Id = @id AND IsDeleted = 0",
            new { id, fullName, now }, cancellationToken: ct));
    }

    public async Task UpdatePhotoAsync(int id, string? path, DateTime now, CancellationToken ct = default)
    {
        await using var c = await db.OpenAsync(ct);
        await c.ExecuteAsync(new CommandDefinition(
            "UPDATE auth.Users SET ProfileImagePath = @path, UpdatedAt = @now, UpdatedBy = @id WHERE Id = @id AND IsDeleted = 0",
            new { id, path, now }, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<AdminUserDto>> ListForAdminAsync(DateTime today, DateTime now, CancellationToken ct = default)
    {
        await using var c = await db.OpenAsync(ct);
        var rows = await c.QueryAsync<AdminUserDto>(new CommandDefinition(
            """
            SELECT u.Id, u.Email, u.FullName, u.RoleId AS Role, u.ProfileImagePath AS ProfileImage, u.DailyTokenLimit,
                   u.IsActive, u.MustChangePassword,
                   CAST(CASE WHEN u.LockoutEndAt > @now THEN 1 ELSE 0 END AS bit) AS IsLocked,
                   u.CreatedAt, u.LastLoginAt, ISNULL(ds.AiTokens, 0) AS TodayTokens
            FROM auth.Users u
            LEFT JOIN study.DailyStats ds ON ds.UserId = u.Id AND ds.StatDate = @today
            WHERE u.IsDeleted = 0
            ORDER BY u.FullName
            """, new { today, now }, cancellationToken: ct));
        return rows.ToList();
    }

    public async Task UpdateByAdminAsync(int id, UpdateUserRequest req, int updatedBy, DateTime now, CancellationToken ct = default)
    {
        await using var c = await db.OpenAsync(ct);
        // Rol değişirse SecurityStamp yenilenir, kullanıcının token'ı yeni rolle yeniden üretilir
        await c.ExecuteAsync(new CommandDefinition(
            """
            UPDATE auth.Users
            SET Email = @Email, FullName = @FullName, DailyTokenLimit = @DailyTokenLimit,
                SecurityStamp = CASE WHEN RoleId <> @RoleId THEN NEWID() ELSE SecurityStamp END,
                RoleId = @RoleId, UpdatedAt = @now, UpdatedBy = @updatedBy
            WHERE Id = @id AND IsDeleted = 0
            """,
            new
            {
                id, Email = new DbString { Value = req.Email, IsAnsi = true, Length = 256 },
                req.FullName, req.DailyTokenLimit, RoleId = (byte)req.Role, updatedBy, now
            }, cancellationToken: ct));
    }

    public async Task SetActiveAsync(int id, bool isActive, int updatedBy, DateTime now, CancellationToken ct = default)
    {
        await using var c = await db.OpenAsync(ct);
        await c.ExecuteAsync(new CommandDefinition(
            """
            UPDATE auth.Users
            SET IsActive = @isActive, SecurityStamp = NEWID(), UpdatedAt = @now, UpdatedBy = @updatedBy
            WHERE Id = @id AND IsDeleted = 0
            """, new { id, isActive, updatedBy, now }, cancellationToken: ct));
    }

    public async Task SoftDeleteAsync(int id, int deletedBy, DateTime now, CancellationToken ct = default)
    {
        await using var c = await db.OpenAsync(ct);
        await c.ExecuteAsync(new CommandDefinition(
            """
            UPDATE auth.Users
            SET IsDeleted = 1, IsActive = 0, SecurityStamp = NEWID(), DeletedAt = @now, DeletedBy = @deletedBy
            WHERE Id = @id AND IsDeleted = 0
            """, new { id, deletedBy, now }, cancellationToken: ct));
    }

    public async Task UnlockAsync(int id, CancellationToken ct = default)
    {
        await using var c = await db.OpenAsync(ct);
        await c.ExecuteAsync(new CommandDefinition(
            "UPDATE auth.Users SET FailedLoginCount = 0, LockoutEndAt = NULL WHERE Id = @id", new { id }, cancellationToken: ct));
    }
}
