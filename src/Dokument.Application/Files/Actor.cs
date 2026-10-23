namespace Dokument.Application.Files;

/// <summary>Who is acting, and from where, for permission checks and the audit log (docs/design.md §18).</summary>
public sealed record Actor(Guid UserId, string? Ip = null, string? UserAgent = null);
