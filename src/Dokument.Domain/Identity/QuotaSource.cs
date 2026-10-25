namespace Dokument.Domain.Identity;

/// <summary>Who set a person's quota (docs/design.md §17.1): an admin on the People page, or the IdP's quota claim.</summary>
public enum QuotaSource
{
    Admin,
    Claim,
}
