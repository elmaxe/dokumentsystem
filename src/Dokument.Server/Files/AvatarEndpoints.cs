using Dokument.Domain.Previews;
using Dokument.Infrastructure.Persistence;
using Dokument.Infrastructure.Previews;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Net.Http.Headers;

namespace Dokument.Server.Files;

/// <summary>Profile pictures (docs/design.md §5.1).</summary>
public static class AvatarEndpoints
{
    public static RouteGroupBuilder MapAvatarEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/people/{id:guid}/avatar", GetAvatar)
            .WithName("GetAvatar")
            .WithTags("People")
            .RequireAuthorization()
            .WithDescription(
                "A person's profile picture: a 256 px square WebP from their sign-in provider. 404 when they have none, so clients show initials. Pass the person's avatar as v; such a URL never changes and is cached for a year. Not audited."
            );
        return api;
    }

    private static async Task<Results<PhysicalFileHttpResult, NotFound>> GetAvatar(
        Guid id,
        string? v,
        HttpContext context,
        DokumentDbContext db,
        DerivedStore derived,
        CancellationToken ct
    )
    {
        var version = await db
            .Users.AsNoTracking()
            .Where(u => u.Id == id)
            .Select(u => u.AvatarVersion)
            .FirstOrDefaultAsync(ct);
        var path = derived.PathOf(DerivedKey.ForAvatar(id));
        if (version is null || !File.Exists(path))
        {
            return TypedResults.NotFound();
        }

        // With the version in the URL, a new picture is a new URL, so this one can be kept for good.
        context.Response.Headers.CacheControl =
            v == version ? "private, max-age=31536000, immutable" : "private, no-cache";
        context.Response.Headers.XContentTypeOptions = "nosniff";
        return TypedResults.PhysicalFile(
            path,
            "image/webp",
            entityTag: new EntityTagHeaderValue($"\"{version}\"")
        );
    }
}
