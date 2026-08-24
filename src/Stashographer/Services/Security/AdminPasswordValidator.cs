using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace Stashographer.Services.Security;

/// <summary>Validates the deployment-supplied administrator password without retaining plaintext.</summary>
public sealed class AdminPasswordValidator
{
    public const string PasswordEnvironmentVariable = "STASHOGRAPHER_ADMIN_PASSWORD";
    private const string PasswordStampClaim = "stashographer:password-stamp";
    private readonly byte[] _expectedHash;
    private readonly string _stamp;

    public AdminPasswordValidator()
    {
        var password = Environment.GetEnvironmentVariable(PasswordEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(password))
            throw new InvalidOperationException($"Set {PasswordEnvironmentVariable} before starting Stashographer.");

        _expectedHash = SHA256.HashData(Encoding.UTF8.GetBytes(password));
        _stamp = Convert.ToHexString(_expectedHash.AsSpan(0, 12));
    }

    public bool IsValid(string? candidate)
    {
        var candidateHash = SHA256.HashData(Encoding.UTF8.GetBytes(candidate ?? string.Empty));
        return CryptographicOperations.FixedTimeEquals(_expectedHash, candidateHash);
    }

    public bool IsCurrent(ClaimsPrincipal? principal) =>
        string.Equals(principal?.FindFirstValue(PasswordStampClaim), _stamp, StringComparison.Ordinal);

    public ClaimsPrincipal CreatePrincipal()
    {
        var identity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.Name, "Administrator"),
                new Claim(ClaimTypes.Role, "Administrator"),
                new Claim(PasswordStampClaim, _stamp)
            ],
            CookieAuthenticationDefaults.AuthenticationScheme);
        return new ClaimsPrincipal(identity);
    }
}
