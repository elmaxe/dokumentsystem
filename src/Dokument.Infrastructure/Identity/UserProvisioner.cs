using Dokument.Application.Identity;
using Dokument.Domain.Identity;
using Dokument.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Dokument.Infrastructure.Identity;

/// <summary>Creates users on first login and refreshes their profile and role on every login (docs/design.md §5).</summary>
public sealed class UserProvisioner(DokumentDbContext db, TimeProvider time)
{
    public async Task<ProvisioningResult> ProvisionAsync(
        OidcIdentity identity,
        LoginPolicy policy,
        CancellationToken ct = default
    )
    {
        var decision = policy.Evaluate(identity.Groups);
        if (!decision.Allowed)
        {
            return ProvisioningResult.Fail(LoginFailure.NotInUserGroup);
        }

        try
        {
            return await UpsertAsync(identity, decision.Role, ct);
        }
        catch (DbUpdateException)
        {
            // Two first logins raced on the unique (issuer, subject) index. The other one created the user; update it.
            db.ChangeTracker.Clear();
            return await UpsertAsync(identity, decision.Role, ct);
        }
    }

    private async Task<ProvisioningResult> UpsertAsync(
        OidcIdentity identity,
        UserRole role,
        CancellationToken ct
    )
    {
        var now = time.GetUtcNow();
        var user = await db.Users.SingleOrDefaultAsync(
            u => u.OidcIssuer == identity.Issuer && u.OidcSubject == identity.Subject,
            ct
        );
        if (user is null)
        {
            user = new User
            {
                OidcIssuer = identity.Issuer,
                OidcSubject = identity.Subject,
                DisplayName = DisplayNameFor(identity),
                CreatedAt = now,
            };
            db.Users.Add(user);
        }
        else if (user.IsDisabled)
        {
            return ProvisioningResult.Fail(LoginFailure.Disabled);
        }

        user.Email = identity.Email;
        user.DisplayName = DisplayNameFor(identity);
        user.Role = role;
        user.LastLoginAt = now;
        await db.SaveChangesAsync(ct);
        return new ProvisioningResult(user, null);
    }

    private static string DisplayNameFor(OidcIdentity identity) =>
        !string.IsNullOrWhiteSpace(identity.DisplayName) ? identity.DisplayName.Trim()
        : !string.IsNullOrWhiteSpace(identity.Email) ? identity.Email.Trim()
        : identity.Subject;
}

public sealed record ProvisioningResult(User? User, LoginFailure? Failure)
{
    public static ProvisioningResult Fail(LoginFailure failure) => new(null, failure);
}

public enum LoginFailure
{
    NotInUserGroup,
    Disabled,
}
