using Microsoft.AspNetCore.Identity;

namespace EnglishApp.Api.Infrastructure.Security;

/// <summary>Şifre hash'leme: .NET PasswordHasher (PBKDF2-HMAC-SHA512, tuzlu).</summary>
public interface IPasswordService
{
    string Hash(string password);
    PasswordCheck Verify(string hash, string password);
}

public enum PasswordCheck { Failed, Success, SuccessNeedsRehash }

public sealed class PasswordService : IPasswordService
{
    private static readonly PasswordHasher<object> Hasher = new();
    private static readonly object Dummy = new();

    public string Hash(string password) => Hasher.HashPassword(Dummy, password);

    public PasswordCheck Verify(string hash, string password)
    {
        try
        {
            return Hasher.VerifyHashedPassword(Dummy, hash, password) switch
            {
                PasswordVerificationResult.Success => PasswordCheck.Success,
                PasswordVerificationResult.SuccessRehashNeeded => PasswordCheck.SuccessNeedsRehash,
                _ => PasswordCheck.Failed
            };
        }
        catch (FormatException)
        {
            return PasswordCheck.Failed; // bozuk hash
        }
    }
}
