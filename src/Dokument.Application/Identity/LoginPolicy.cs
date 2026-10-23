using Dokument.Domain.Identity;

namespace Dokument.Application.Identity;

/// <summary>Maps IdP groups to access (docs/design.md §5). Evaluated on every login, so group changes take effect at the next one.</summary>
public sealed record LoginPolicy(string? UserGroup, string AdminGroup)
{
    public const string DefaultAdminGroup = "dokument-admins";

    public LoginDecision Evaluate(IReadOnlyCollection<string> groups)
    {
        var isAdmin = groups.Contains(AdminGroup, StringComparer.Ordinal);
        if (
            !isAdmin
            && UserGroup is not null
            && !groups.Contains(UserGroup, StringComparer.Ordinal)
        )
        {
            return LoginDecision.Denied;
        }

        return new LoginDecision(Allowed: true, Role: isAdmin ? UserRole.Admin : UserRole.User);
    }
}

public sealed record LoginDecision(bool Allowed, UserRole Role)
{
    public static readonly LoginDecision Denied = new(Allowed: false, Role: UserRole.User);
}
