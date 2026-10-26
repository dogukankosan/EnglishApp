using System.Data;
using Microsoft.Data.SqlClient;

namespace EnglishApp.Api.Infrastructure.Data;

/// <summary>
/// Kısa ömürlü bağlantı üretir. Bağlantılar connection pool'dan gelir,
/// "using" ile hemen kapatılır.
/// </summary>
public interface IDbConnectionFactory
{
    Task<SqlConnection> OpenAsync(CancellationToken ct = default);
}

public sealed class DbConnectionFactory(IConfiguration configuration) : IDbConnectionFactory
{
    private readonly string _connectionString = configuration.GetConnectionString("Default")
        ?? throw new InvalidOperationException("ConnectionStrings:Default tanımlı değil.");

    public async Task<SqlConnection> OpenAsync(CancellationToken ct = default)
    {
        var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(ct);
        return connection;
    }
}

public static class DbExtensions
{
    /// <summary>Transaction içinde iş çalıştırır; hata olursa geri alır.</summary>
    public static async Task<T> InTransactionAsync<T>(this IDbConnectionFactory factory,
        Func<SqlConnection, SqlTransaction, Task<T>> work, CancellationToken ct = default,
        IsolationLevel isolation = IsolationLevel.ReadCommitted)
    {
        await using var connection = await factory.OpenAsync(ct);
        await using var tx = (SqlTransaction)await connection.BeginTransactionAsync(isolation, ct);
        try
        {
            var result = await work(connection, tx);
            await tx.CommitAsync(ct);
            return result;
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None);
            throw;
        }
    }
}
