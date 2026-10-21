using System.Text.Json;

namespace Dokument.Infrastructure.Persistence;

/// <summary>An admin setting edited in the UI (docs/design.md §4.3). Bootstrap config stays in environment variables.</summary>
public sealed class Setting
{
    public required string Key { get; set; }
    public required JsonDocument Value { get; set; }
    public Guid? UpdatedBy { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
