using System.Text.Json.Nodes;
using Dokument.Domain.Audit;
using Dokument.Domain.Identity;
using Dokument.Domain.Settings;
using Dokument.Infrastructure.Files;
using Dokument.Infrastructure.Identity;
using Dokument.Infrastructure.Live;
using Dokument.Infrastructure.Ml;
using Dokument.Infrastructure.Persistence;
using Dokument.Infrastructure.Previews;
using Dokument.Infrastructure.Search;
using Dokument.Infrastructure.Settings;
using Dokument.Infrastructure.Storage;
using Dokument.Server.Auth;
using Dokument.Server.Files;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Dokument.Server.Admin;

/// <summary>The admin settings page (docs/design.md §4.3, §11, §17, §21). Admins only.</summary>
public static class AdminEndpoints
{
    public static RouteGroupBuilder MapAdminEndpoints(this RouteGroupBuilder api)
    {
        var admin = api.MapGroup("/admin")
            .WithTags("Admin")
            .RequireAuthorization(SessionAuthentication.AdminPolicy);
        admin.MapGet("/settings", GetSettings).WithName("AdminGetSettings");
        admin.MapPut("/settings/storage", PutStorage).WithName("AdminPutStorageSettings");
        admin.MapPut("/settings/versions", PutVersions).WithName("AdminPutVersionSettings");
        admin.MapPut("/settings/trash", PutTrash).WithName("AdminPutTrashSettings");
        admin.MapPut("/settings/ml", PutMl).WithName("AdminPutMlSettings");
        admin.MapPut("/settings/scanner", PutScanner).WithName("AdminPutScannerSettings");
        admin
            .MapPut("/settings/text-recognition", PutTextRecognition)
            .WithName("AdminPutTextRecognitionSettings")
            .WithDescription(
                "Turning it on reads again, in the background, the files skipped while it was off."
            );
        admin
            .MapGet("/text-recognition/progress", GetTextProgress)
            .WithName("AdminGetTextProgress");
        admin
            .MapPost("/text-recognition/reread", RereadAll)
            .WithName("AdminRereadAllText")
            .WithDescription(
                "Reads the text of every stored file again, in the background, after new files are done."
            );
        admin
            .MapGet("/ml/status", GetMlStatus)
            .WithName("AdminGetMlStatus")
            .WithDescription("Checks every saved ML service now.");
        admin.MapPost("/ml/test", TestMl).WithName("AdminTestMl");
        admin.MapPost("/scanner/test", TestScanner).WithName("AdminTestScanner");
        admin.MapPut("/settings/videos", PutVideos).WithName("AdminPutVideoSettings");
        admin.MapPut("/settings/office", PutOffice).WithName("AdminPutOfficeSettings");
        admin.MapPost("/office/test", TestOffice).WithName("AdminTestOffice");
        admin
            .MapGet("/videos/copies", GetVideoCopies)
            .WithName("AdminGetVideoCopies")
            .WithDescription(
                "How many playable copies of videos exist, the disk they take, and how many are still to make."
            );
        admin
            .MapDelete("/videos/copies", DeleteVideoCopies)
            .WithName("AdminDeleteVideoCopies")
            .WithDescription(
                "Deletes every playable copy. The originals are untouched; copies are made again as the setting says."
            );
        admin
            .MapPut("/settings/transcription", PutTranscription)
            .WithName("AdminPutTranscriptionSettings")
            .WithDescription(
                "Turning it on, or including videos, queues the recordings not transcribed yet, in the background."
            );
        admin
            .MapGet("/transcription/progress", GetTranscriptionProgress)
            .WithName("AdminGetTranscriptionProgress");
        admin
            .MapPost("/transcription/redo", RedoAllTranscripts)
            .WithName("AdminRedoAllTranscripts")
            .WithDescription(
                "Transcribes every stored recording again, in the background, e.g. after picking another model. Old transcripts stay searchable until each is redone."
            );
        admin.MapGet("/storage", GetStorageReport).WithName("AdminGetStorage");
        admin.MapGet("/users", ListUsers).WithName("AdminListUsers");
        admin.MapPut("/users/{id:guid}/quota", PutUserQuota).WithName("AdminPutUserQuota");
        admin.MapJobEndpoints();
        return api;
    }

    private static async Task<Ok<AdminSettingsDto>> GetSettings(
        SettingsStore settings,
        OidcSettings oidc,
        OfficeAddress officeAddress,
        CancellationToken ct
    )
    {
        var storage = await settings.GetAsync(SettingKeys.Storage, ct);
        var versions = await settings.GetAsync(SettingKeys.Versions, ct);
        var trash = await settings.GetAsync(SettingKeys.Trash, ct);
        var ml = await settings.GetAsync(SettingKeys.Ml, ct);
        var scanner = await settings.GetAsync(SettingKeys.Scanner, ct);
        var textRecognition = await settings.GetAsync(SettingKeys.TextRecognition, ct);
        var videos = await settings.GetAsync(SettingKeys.Videos, ct);
        var office = await officeAddress.GetAsync(settings, ct);
        var transcription = await settings.GetAsync(SettingKeys.Transcription, ct);
        return TypedResults.Ok(
            new AdminSettingsDto(
                new StorageSettingsDto(storage.DefaultQuota.Bytes),
                new VersionSettingsDto(versions.KeepDays, versions.MaxVersions),
                new TrashSettingsDto(trash.KeepDays),
                MlDto(ml),
                new ScannerSettingsDto(scanner.Address?.ToString()),
                new TextRecognitionDto(textRecognition.Enabled),
                new VideoSettingsDto(videos.Copies, videos.MaxHeight),
                new OfficeSettingsDto(
                    office.GotenbergUrl?.Value,
                    officeAddress.Configured is not null
                ),
                TranscriptionDto(transcription),
                new SignInInfo(
                    oidc.Enabled,
                    oidc.ProviderName,
                    oidc.Issuer,
                    oidc.Policy.UserGroup,
                    oidc.Policy.AdminGroup,
                    oidc.QuotaClaim
                )
            )
        );
    }

    private static async Task<Results<Ok<StorageSettingsDto>, ValidationProblem>> PutStorage(
        StorageSettingsDto request,
        HttpContext context,
        SettingsStore settings,
        LiveChanges live,
        CancellationToken ct
    )
    {
        if (request.DefaultQuotaBytes is <= 0)
        {
            return Invalid("defaultQuotaBytes", "A limit must be more than 0 bytes.");
        }

        var value = new StorageSettings(StorageLimit.From(request.DefaultQuotaBytes));
        await settings.SetAsync(SettingKeys.Storage, value, context.Actor(), ct);
        await live.NotifyEveryoneAsync(LiveTopics.Storage, ct);
        return TypedResults.Ok(request);
    }

    private static async Task<Results<Ok<VersionSettingsDto>, ValidationProblem>> PutVersions(
        VersionSettingsDto request,
        HttpContext context,
        SettingsStore settings,
        CancellationToken ct
    )
    {
        var value = new VersionSettings(request.KeepDays, request.MaxVersions);
        if (!value.IsValid)
        {
            return Invalid(
                "versions",
                $"Keep versions for 1 to {VersionSettings.MaxKeepDays} days, and 1 to {VersionSettings.MaxMaxVersions} per file."
            );
        }

        await settings.SetAsync(SettingKeys.Versions, value, context.Actor(), ct);
        return TypedResults.Ok(request);
    }

    private static async Task<Results<Ok<TrashSettingsDto>, ValidationProblem>> PutTrash(
        TrashSettingsDto request,
        HttpContext context,
        SettingsStore settings,
        CancellationToken ct
    )
    {
        var value = new TrashSettings(request.KeepDays);
        if (!value.IsValid)
        {
            return Invalid(
                "keepDays",
                $"Keep items in the trash for 1 to {TrashSettings.MaxKeepDays} days."
            );
        }

        await settings.SetAsync(SettingKeys.Trash, value, context.Actor(), ct);
        return TypedResults.Ok(request);
    }

    private static async Task<Ok<TextRecognitionDto>> PutTextRecognition(
        TextRecognitionDto request,
        HttpContext context,
        SettingsStore settings,
        ContentService content,
        TimeProvider time,
        CancellationToken ct
    )
    {
        var was = await settings.GetAsync(SettingKeys.TextRecognition, ct);
        await settings.SetAsync(
            SettingKeys.TextRecognition,
            new TextRecognitionSettings(request.Enabled),
            context.Actor(),
            ct
        );
        if (request.Enabled && !was.Enabled)
        {
            await content.EnqueueSkippedAsync(time.GetUtcNow(), ct);
        }

        return TypedResults.Ok(request);
    }

    private static async Task<Results<Ok<VideoSettingsDto>, ValidationProblem>> PutVideos(
        VideoSettingsDto request,
        HttpContext context,
        SettingsStore settings,
        MediaService media,
        TimeProvider time,
        CancellationToken ct
    )
    {
        var value = new VideoSettings(request.Copies, request.MaxHeight);
        if (!value.IsValid)
        {
            return Invalid(
                "maxHeight",
                "Copies are 720 or 1080 pixels high, or the original's size."
            );
        }

        var was = await settings.GetAsync(SettingKeys.Videos, ct);
        await settings.SetAsync(SettingKeys.Videos, value, context.Actor(), ct);
        if (value.Copies == VideoCopies.Background && was.Copies != VideoCopies.Background)
        {
            await media.EnqueueMissingCopiesAsync(time.GetUtcNow(), ct);
        }

        return TypedResults.Ok(request);
    }

    private static async Task<Results<Ok<OfficeSettingsDto>, ValidationProblem>> PutOffice(
        OfficeSettingsRequest request,
        HttpContext context,
        SettingsStore settings,
        PreviewService previews,
        OfficeAddress officeAddress,
        TimeProvider time,
        CancellationToken ct
    )
    {
        if (officeAddress.Configured is not null)
        {
            return Invalid(
                "gotenbergUrl",
                $"The address is set by {OfficeAddress.ConfigKey} in the server's configuration."
            );
        }

        ServiceUrl? url = null;
        if (!string.IsNullOrWhiteSpace(request.GotenbergUrl))
        {
            url = ServiceUrl.TryParse(request.GotenbergUrl, out var error);
            if (url is null)
            {
                return Invalid("gotenbergUrl", error!);
            }
        }

        var was = await settings.GetAsync(SettingKeys.Office, ct);
        await settings.SetAsync(SettingKeys.Office, new OfficeSettings(url), context.Actor(), ct);
        if (url is not null && was.GotenbergUrl is null)
        {
            // Documents uploaded while previews were off get them now.
            await previews.EnqueueOfficeAsync(time.GetUtcNow(), ct);
        }

        return TypedResults.Ok(new OfficeSettingsDto(url?.Value, false));
    }

    private static async Task<Results<Ok<ProbeResult>, ValidationProblem>> TestOffice(
        TestOfficeRequest request,
        ServiceProbe probe,
        CancellationToken ct
    ) =>
        ServiceUrl.TryParse(request.Url, out var error) is { } url
            ? TypedResults.Ok(await probe.CheckGotenbergAsync(url, ct))
            : Invalid("url", error!);

    private static async Task<
        Results<Ok<TranscriptionSettingsDto>, ValidationProblem>
    > PutTranscription(
        UpdateTranscriptionRequest request,
        HttpContext context,
        SettingsStore settings,
        TranscriptService transcripts,
        TimeProvider time,
        CancellationToken ct
    )
    {
        var value = new TranscriptionSettings(request.Enabled, request.Model, request.Videos);
        if (!value.IsValid)
        {
            return Invalid(
                "model",
                $"Use one of {string.Join(", ", TranscriptionSettings.Models)}."
            );
        }

        await settings.SetAsync(SettingKeys.Transcription, value, context.Actor(), ct);
        await transcripts.DropUnwantedAsync(ct);
        await transcripts.EnqueueMissingAsync(time.GetUtcNow(), ct);
        return TypedResults.Ok(TranscriptionDto(value));
    }

    private static async Task<Ok<TranscriptionProgress>> GetTranscriptionProgress(
        TranscriptService transcripts,
        CancellationToken ct
    ) => TypedResults.Ok(await transcripts.ProgressAsync(ct));

    private static async Task<Ok<TranscriptionProgress>> RedoAllTranscripts(
        TranscriptService transcripts,
        TimeProvider time,
        CancellationToken ct
    )
    {
        await transcripts.RedoAllAsync(time.GetUtcNow(), ct);
        return TypedResults.Ok(await transcripts.ProgressAsync(ct));
    }

    private static TranscriptionSettingsDto TranscriptionDto(TranscriptionSettings value) =>
        new(value.Enabled, value.Model, value.Videos, [.. TranscriptionSettings.Models]);

    private static async Task<Ok<CopyStats>> GetVideoCopies(
        MediaService media,
        CancellationToken ct
    ) => TypedResults.Ok(await media.CopyStatsAsync(ct));

    private static async Task<Ok<CopyStats>> DeleteVideoCopies(
        HttpContext context,
        MediaService media,
        SettingsStore settings,
        TimeProvider time,
        CancellationToken ct
    )
    {
        await media.DeleteCopiesAsync(ct);
        if ((await settings.GetAsync(SettingKeys.Videos, ct)).Copies == VideoCopies.Background)
        {
            await media.EnqueueMissingCopiesAsync(time.GetUtcNow(), ct);
        }

        return TypedResults.Ok(await media.CopyStatsAsync(ct));
    }

    private static async Task<Ok<TextProgress>> GetTextProgress(
        ContentService content,
        CancellationToken ct
    ) => TypedResults.Ok(await content.ProgressAsync(ct));

    private static async Task<Ok<TextProgress>> RereadAll(
        ContentService content,
        TimeProvider time,
        CancellationToken ct
    )
    {
        await content.EnqueueAllAsync(time.GetUtcNow(), ct);
        return TypedResults.Ok(await content.ProgressAsync(ct));
    }

    private static async Task<Results<Ok<MlSettingsDto>, ValidationProblem>> PutMl(
        UpdateMlSettingsRequest request,
        HttpContext context,
        SettingsStore settings,
        SecretProtector secrets,
        TranscriptService transcripts,
        TimeProvider time,
        CancellationToken ct
    )
    {
        if (request.Endpoints.Count > MlSettings.MaxEndpoints)
        {
            return Invalid("endpoints", $"Add at most {MlSettings.MaxEndpoints} ML services.");
        }

        var saved = (await settings.GetAsync(SettingKeys.Ml, ct)).Endpoints;
        var endpoints = new List<MlEndpoint>();
        for (var i = 0; i < request.Endpoints.Count; i++)
        {
            var update = request.Endpoints[i];
            if (ServiceUrl.TryParse(update.Url, out var error) is not { } url)
            {
                return Invalid($"endpoints[{i}].url", error!);
            }

            if (endpoints.Any(e => e.Url == url))
            {
                return Invalid($"endpoints[{i}].url", "This ML service is already in the list.");
            }

            var key =
                update.KeepApiKey ? saved.FirstOrDefault(e => e.Url == url)?.ProtectedApiKey
                : string.IsNullOrWhiteSpace(update.ApiKey) ? null
                : secrets.Protect(update.ApiKey.Trim());
            endpoints.Add(new MlEndpoint(url, key));
        }

        var value = new MlSettings(endpoints);
        await settings.SetAsync(SettingKeys.Ml, value, context.Actor(), ct);

        // The first ML service makes the recordings waiting for one transcribable (§11.1).
        await transcripts.EnqueueMissingAsync(time.GetUtcNow(), ct);
        return TypedResults.Ok(MlDto(value));
    }

    private static async Task<Results<Ok<ScannerSettingsDto>, ValidationProblem>> PutScanner(
        ScannerSettingsDto request,
        HttpContext context,
        SettingsStore settings,
        CancellationToken ct
    )
    {
        ClamdAddress? address = null;
        if (!string.IsNullOrWhiteSpace(request.Address))
        {
            address = ClamdAddress.TryParse(request.Address, out var error);
            if (address is null)
            {
                return Invalid("address", error!);
            }
        }

        await settings.SetAsync(
            SettingKeys.Scanner,
            new ScannerSettings(address),
            context.Actor(),
            ct
        );
        return TypedResults.Ok(new ScannerSettingsDto(address?.ToString()));
    }

    private static async Task<Ok<List<MlEndpointStatus>>> GetMlStatus(
        MlServices services,
        ServiceProbe probe,
        CancellationToken ct
    )
    {
        var results = await Task.WhenAll(
            (await services.ListAsync(ct)).Select(async e => new MlEndpointStatus(
                e.Url.Value,
                await probe.CheckMlAsync(e.Url, e.ApiKey, ct),
                e.FromEnvironment
            ))
        );
        return TypedResults.Ok(results.ToList());
    }

    private static async Task<Results<Ok<ProbeResult>, ValidationProblem>> TestMl(
        TestMlRequest request,
        SettingsStore settings,
        SecretProtector secrets,
        ServiceProbe probe,
        CancellationToken ct
    )
    {
        if (ServiceUrl.TryParse(request.Url, out var error) is not { } url)
        {
            return Invalid("url", error!);
        }

        var apiKey = request.ApiKey;
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            var saved = (await settings.GetAsync(SettingKeys.Ml, ct)).Endpoints.FirstOrDefault(e =>
                e.Url == url
            );
            apiKey = saved is null ? null : ApiKeyOf(saved, secrets);
        }

        return TypedResults.Ok(await probe.CheckMlAsync(url, apiKey?.Trim(), ct));
    }

    private static async Task<Results<Ok<ProbeResult>, ValidationProblem>> TestScanner(
        TestScannerRequest request,
        ServiceProbe probe,
        CancellationToken ct
    ) =>
        ClamdAddress.TryParse(request.Address, out var error) is { } address
            ? TypedResults.Ok(await probe.CheckClamdAsync(address, ct))
            : Invalid("address", error!);

    private static async Task<Ok<StorageReport>> GetStorageReport(
        DokumentDbContext db,
        IOptions<StorageOptions> storage,
        CancellationToken ct
    )
    {
        var filesBytes = await db.Blobs.SumAsync(b => (long?)b.Size, ct) ?? 0;
        var paths = new[]
        {
            ("originals", storage.Value.OriginalsPath),
            ("derived", storage.Value.DerivedPath),
        };
        var drives = DriveInfo.GetDrives().Where(d => d.IsReady).ToList();
        var volumes = paths
            .Select(p => (Holds: p.Item1, Drive: DriveOf(drives, p.Item2)))
            .Where(p => p.Drive is not null)
            .GroupBy(p => p.Drive!.RootDirectory.FullName)
            .Select(g => new VolumeUsage(
                g.Select(p => p.Holds).ToList(),
                g.First().Drive!.TotalSize,
                g.First().Drive!.AvailableFreeSpace
            ))
            .ToList();
        return TypedResults.Ok(new StorageReport(filesBytes, volumes));
    }

    private static async Task<Ok<List<AdminUser>>> ListUsers(
        DokumentDbContext db,
        SettingsStore settings,
        OidcSettings oidc,
        CancellationToken ct
    )
    {
        var defaultQuota = (await settings.GetAsync(SettingKeys.Storage, ct)).DefaultQuota;
        var users = await db.Users.AsNoTracking().OrderBy(u => u.DisplayName).ToListAsync(ct);
        return TypedResults.Ok(users.Select(u => AdminUserOf(u, defaultQuota, oidc)).ToList());
    }

    private static async Task<Results<Ok<AdminUser>, NotFound, ValidationProblem>> PutUserQuota(
        Guid id,
        QuotaDto request,
        HttpContext context,
        DokumentDbContext db,
        SettingsStore settings,
        OidcSettings oidc,
        AuditLog audit,
        LiveChanges live,
        CancellationToken ct
    )
    {
        UserQuota quota;
        switch (request.Kind)
        {
            case UserQuotaKind.Limit when request.Bytes is > 0:
                quota = UserQuota.Limit(request.Bytes.Value);
                break;
            case UserQuotaKind.Limit:
                return Invalid("bytes", "A limit must be more than 0 bytes.");
            case UserQuotaKind.Unlimited:
                quota = UserQuota.Unlimited;
                break;
            default:
                quota = UserQuota.Default;
                break;
        }

        var user = await db.Users.SingleOrDefaultAsync(u => u.Id == id, ct);
        if (user is null)
        {
            return TypedResults.NotFound();
        }

        if (QuotaFromClaim(user, oidc))
        {
            return Invalid(
                "kind",
                "This person's storage is set by the sign-in provider. Change it there."
            );
        }

        var before = user.Quota;
        user.SetQuota(quota, QuotaSource.Admin);
        audit.Add(
            context.Actor(),
            AuditActions.QuotaChange,
            details: new JsonObject
            {
                ["userId"] = id,
                ["from"] = QuotaAudit.Json(before),
                ["to"] = QuotaAudit.Json(quota),
                ["source"] = QuotaAudit.SourceName(QuotaSource.Admin),
            }
        );
        await db.SaveChangesAsync(ct);
        await live.NotifyAsync(user.Id, LiveTopics.Storage, ct);
        var defaultQuota = (await settings.GetAsync(SettingKeys.Storage, ct)).DefaultQuota;
        return TypedResults.Ok(AdminUserOf(user, defaultQuota, oidc));
    }

    /// <summary>
    /// Whether the IdP sets this person's quota (§17.1): the claim wins, so admins can't change it. If the claim is no
    /// longer configured, admins take the quota over again.
    /// </summary>
    private static bool QuotaFromClaim(User user, OidcSettings oidc) =>
        user.QuotaSource == QuotaSource.Claim && oidc.QuotaClaim is not null;

    private static AdminUser AdminUserOf(User user, StorageLimit defaultQuota, OidcSettings oidc) =>
        new(
            user.Id,
            user.DisplayName,
            user.Email,
            user.Role,
            user.UsedBytes,
            QuotaDto.From(user.Quota),
            QuotaFromClaim(user, oidc) ? QuotaSource.Claim : QuotaSource.Admin,
            user.Quota.Resolve(defaultQuota),
            user.LastLoginAt,
            user.IsDisabled,
            user.AvatarVersion
        );

    private static MlSettingsDto MlDto(MlSettings ml) =>
        new(
            ml.Endpoints.Select(e => new MlEndpointDto(e.Url.Value, e.ProtectedApiKey is not null))
                .ToList()
        );

    private static string? ApiKeyOf(MlEndpoint endpoint, SecretProtector secrets) =>
        endpoint.ProtectedApiKey is { } key ? secrets.Unprotect(key) : null;

    /// <summary>The mounted volume a path lives on: the drive with the longest root that contains it.</summary>
    private static DriveInfo? DriveOf(List<DriveInfo> drives, string path)
    {
        var full = Path.GetFullPath(path);
        return drives
            .Where(d =>
                full.StartsWith(d.RootDirectory.FullName, StringComparison.Ordinal)
                && (
                    full.Length == d.RootDirectory.FullName.Length
                    || d.RootDirectory.FullName.EndsWith('/')
                    || full[d.RootDirectory.FullName.Length] == '/'
                )
            )
            .MaxBy(d => d.RootDirectory.FullName.Length);
    }

    private static ValidationProblem Invalid(string field, string message) =>
        TypedResults.ValidationProblem(new Dictionary<string, string[]> { [field] = [message] });
}
