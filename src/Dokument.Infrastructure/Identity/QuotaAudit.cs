using System.Text.Json.Nodes;
using Dokument.Domain.Identity;

namespace Dokument.Infrastructure.Identity;

/// <summary>How quotas appear in <c>user.quota_change</c> audit details (docs/design.md §17, §18).</summary>
public static class QuotaAudit
{
    public static JsonObject Json(UserQuota quota) =>
        new() { ["kind"] = quota.Kind.ToString().ToLowerInvariant(), ["bytes"] = quota.Bytes };

    public static string SourceName(QuotaSource source) => source.ToString().ToLowerInvariant();
}
