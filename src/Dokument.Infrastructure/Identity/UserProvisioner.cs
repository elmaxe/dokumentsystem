using System.Text.Json.Nodes;
using Dokument.Application.Identity;
using Dokument.Domain.Audit;
using Dokument.Domain.Identity;
using Dokument.Infrastructure.Files;
using Dokument.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Dokument.Infrastructure.Identity;

/// <summary>
/// Creates users on first login and refreshes their profile, role and claimed quota on every login (docs/design.md §5,
/// §17.1).
/// </summary>
public sealed class UserProvisioner(DokumentDbContext db, AuditLog audit, TimeProvider time)
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

        var before = user.Quota;
        // A claim that went away also counts as the claim's change, even though admins own the quota afterwards.
        if (identity.Quota.ApplyTo(user))
        {
            audit.AddSystem(
                AuditActions.QuotaChange,
                details: new JsonObject
                {
                    ["userId"] = user.Id,
                    ["from"] = QuotaAudit.Json(before),
                    ["to"] = QuotaAudit.Json(user.Quota),
                    ["source"] = QuotaAudit.SourceName(QuotaSource.Claim),
                }
            );
        }

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
