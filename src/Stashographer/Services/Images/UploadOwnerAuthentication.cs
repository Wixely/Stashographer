using System.Security.Claims;

namespace Stashographer.Services.Images;

/// <summary>
/// Gives each household browser a protected, stable upload identity without requiring the
/// administrator login. DNAX uses this identity to isolate resumable upload sessions.
/// </summary>
public static class UploadOwnerAuthentication
{
    public const string Scheme = "Stashographer.UploadOwner";
    public const string Policy = "Stashographer.Uploads";

    public static ClaimsIdentity CreateIdentity() => new(
        [new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString("N"))],
        Scheme,
        ClaimTypes.Name,
        ClaimTypes.Role);
}
