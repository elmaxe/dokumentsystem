using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;

namespace Dokument.Infrastructure.Settings;

/// <summary>
/// Encrypts secrets kept in admin settings, such as ML API keys (docs/design.md §21), with Data Protection keys from
/// the originals volume. Losing the keys only loses these secrets; an admin enters them again.
/// </summary>
public sealed class SecretProtector(IDataProtectionProvider provider)
{
    private readonly IDataProtector protector = provider.CreateProtector(
        "Dokument.Settings.Secrets"
    );

    public string Protect(string secret) => protector.Protect(secret);

    /// <summary>Null when the keys that protected it are gone.</summary>
    public string? Unprotect(string protectedSecret)
    {
        try
        {
            return protector.Unprotect(protectedSecret);
        }
        catch (CryptographicException)
        {
            return null;
        }
    }
}
