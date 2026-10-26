using EnglishApp.Api.Data;
using EnglishApp.Api.Infrastructure.Security;
using EnglishApp.Api.Infrastructure.Time;
using EnglishApp.Shared.Enums;

namespace EnglishApp.Api.Services;

/// <summary>
/// Uygulama açılışında: hiç aktif admin yoksa appsettings.json'daki bilgilerle admin oluşturur,
/// ardından başlangıç içeriğini (senaryolar/kelimeler) yükler.
/// </summary>
public sealed class StartupSeeder(IServiceProvider services, IConfiguration config, ILogger<StartupSeeder> logger)
{
    public async Task RunAsync(CancellationToken ct = default)
    {
        await using var scope = services.CreateAsyncScope();
        var sp = scope.ServiceProvider;

        try
        {
            await SeedAdminAsync(sp, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "İlk admin oluşturulamadı.");
        }

        try
        {
            await sp.GetRequiredService<IContentSeeder>().SeedAsync(ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Başlangıç içeriği yüklenemedi.");
        }
    }

    private async Task SeedAdminAsync(IServiceProvider sp, CancellationToken ct)
    {
        var users = sp.GetRequiredService<IUserRepository>();
        if (await users.AnyActiveAdminAsync(ct)) return;

        var email = config["Seed:AdminEmail"];
        var password = config["Seed:AdminPassword"];
        var fullName = config["Seed:AdminFullName"] ?? "Admin";
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
        {
            logger.LogWarning("Sistemde admin yok ve appsettings.json'da Seed:AdminEmail / Seed:AdminPassword tanımlı değil.");
            return;
        }

        email = AuthService.NormalizeEmail(email);
        if (await users.EmailExistsAsync(email, null, ct))
        {
            logger.LogWarning("Seed admin e-postası ({Email}) başka bir kullanıcıda kayıtlı, admin oluşturulmadı.", email);
            return;
        }

        var id = await users.CreateAsync(new UserRow
        {
            Email = email,
            FullName = fullName,
            PasswordHash = sp.GetRequiredService<IPasswordService>().Hash(password),
            RoleId = UserRole.Admin,
            DailyTokenLimit = null,
            MustChangePassword = true
        }, null, sp.GetRequiredService<ITrClock>().Now, ct);

        logger.LogWarning("İlk admin oluşturuldu (UserId: {TargetUserId}). İlk girişte şifre değiştirilmesi zorunlu.", id);
    }
}
