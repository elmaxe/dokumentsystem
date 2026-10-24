using System.Text.Json;
using System.Text.Json.Nodes;
using Dokument.Domain.Identity;
using Dokument.Domain.Settings;

namespace Dokument.Infrastructure.Settings;

/// <summary>One row of the <c>settings</c> table: its key, its value when unset, and how it is stored as JSON.</summary>
public sealed record SettingKey<T>(
    string Name,
    T Default,
    Func<T, JsonObject> Write,
    Func<JsonElement, T> Read
);

/// <summary>The admin settings kept in the database (docs/design.md §4.3).</summary>
public static class SettingKeys
{
    public static readonly SettingKey<StorageSettings> Storage = new(
        "storage",
        StorageSettings.Default,
        s => new JsonObject { ["defaultQuotaBytes"] = s.DefaultQuota.Bytes },
        json => new StorageSettings(
            StorageLimit.From(
                json.TryGetProperty("defaultQuotaBytes", out var bytes)
                && bytes.ValueKind == JsonValueKind.Number
                    ? bytes.GetInt64()
                    : null
            )
        )
    );

    public static readonly SettingKey<VersionSettings> Versions = new(
        "versions",
        VersionSettings.Default,
        s => new JsonObject { ["keepDays"] = s.KeepDays, ["maxVersions"] = s.MaxVersions },
        json => new VersionSettings(
            json.GetProperty("keepDays").GetInt32(),
            json.GetProperty("maxVersions").GetInt32()
        )
    );

    public static readonly SettingKey<TrashSettings> Trash = new(
        "trash",
        TrashSettings.Default,
        s => new JsonObject { ["keepDays"] = s.KeepDays },
        json => new TrashSettings(json.GetProperty("keepDays").GetInt32())
    );

    public static readonly SettingKey<MlSettings> Ml = new(
        "ml",
        MlSettings.Default,
        s => new JsonObject
        {
            ["endpoints"] = new JsonArray(
                s.Endpoints.Select(e =>
                        (JsonNode)
                            new JsonObject { ["url"] = e.Url.Value, ["apiKey"] = e.ProtectedApiKey }
                    )
                    .ToArray()
            ),
        },
        json => new MlSettings(
            json.GetProperty("endpoints")
                .EnumerateArray()
                .Select(e => new MlEndpoint(
                    ServiceUrl.Parse(e.GetProperty("url").GetString()!),
                    e.TryGetProperty("apiKey", out var key) ? key.GetString() : null
                ))
                .ToList()
        )
    );

    public static readonly SettingKey<ScannerSettings> Scanner = new(
        "scanner",
        ScannerSettings.Default,
        s => new JsonObject { ["address"] = s.Address?.ToString() },
        json => new ScannerSettings(
            json.TryGetProperty("address", out var address)
            && address.ValueKind == JsonValueKind.String
                ? ClamdAddress.Parse(address.GetString()!)
                : null
        )
    );
}
