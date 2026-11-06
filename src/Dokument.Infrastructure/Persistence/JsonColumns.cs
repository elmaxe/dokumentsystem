using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Dokument.Infrastructure.Persistence;

/// <summary>Lists of small records kept in one <c>jsonb</c> column, compared by their JSON so changes are saved.</summary>
internal static class JsonColumns
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static PropertyBuilder<List<T>> AsJson<T>(this PropertyBuilder<List<T>> property) =>
        property
            .HasColumnType("jsonb")
            .HasConversion(
                v => JsonSerializer.Serialize(v, Options),
                v => JsonSerializer.Deserialize<List<T>>(v, Options) ?? new List<T>(),
                new ValueComparer<List<T>>(
                    (a, b) =>
                        JsonSerializer.Serialize(a, Options)
                        == JsonSerializer.Serialize(b, Options),
                    v => JsonSerializer.Serialize(v, Options).GetHashCode(StringComparison.Ordinal),
                    v =>
                        JsonSerializer.Deserialize<List<T>>(
                            JsonSerializer.Serialize(v, Options),
                            Options
                        )!
                )
            );
}
