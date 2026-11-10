using System.Text.Json;
using System.Text.Json.Nodes;
using Dokument.Application.Files;
using Dokument.Domain.Audit;
using Dokument.Infrastructure.Files;
using Dokument.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Dokument.Infrastructure.Settings;

/// <summary>
/// Reads and writes admin settings (docs/design.md §4.3). A missing or unreadable row gives the setting's default, so
/// a fresh install needs no seeding. Every change is audited (§18).
/// </summary>
public sealed class SettingsStore(
    DokumentDbContext db,
    AuditLog audit,
    TimeProvider time,
    ILogger<SettingsStore> logger
)
{
    public async Task<T> GetAsync<T>(SettingKey<T> key, CancellationToken ct)
    {
        var row = await db.Settings.AsNoTracking().SingleOrDefaultAsync(s => s.Key == key.Name, ct);
        if (row is null)
        {
            return key.Default;
        }

        try
        {
            return key.Read(row.Value.RootElement);
        }
        catch (Exception e)
            when (e
                    is JsonException
                        or FormatException
                        or ArgumentException
                        or KeyNotFoundException
                        or InvalidOperationException
            )
        {
            logger.LogWarning(
                e,
                "The {Setting} setting can't be read; using the default",
                key.Name
            );
            return key.Default;
        }
    }

    public async Task SetAsync<T>(SettingKey<T> key, T value, Actor actor, CancellationToken ct)
    {
        var json = JsonSerializer.SerializeToDocument(key.Write(value));
        var row = await db.Settings.SingleOrDefaultAsync(s => s.Key == key.Name, ct);
        if (row is null)
        {
            db.Settings.Add(
                new Setting
                {
                    Key = key.Name,
                    Value = json,
                    UpdatedBy = actor.UserId,
                    UpdatedAt = time.GetUtcNow(),
                }
            );
        }
        else
        {
            row.Value = json;
            row.UpdatedBy = actor.UserId;
            row.UpdatedAt = time.GetUtcNow();
        }

        // Only the key: values can hold secrets, and the row itself shows the current value.
        audit.Add(
            actor,
            AuditActions.SettingsChange,
            details: new JsonObject { ["key"] = key.Name }
        );
        await db.SaveChangesAsync(ct);
    }
}
