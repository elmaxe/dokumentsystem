namespace Dokument.Domain.Files;

/// <summary>
/// Which old versions are kept (docs/design.md §8.2, decision 2): a non-current version goes once it has been
/// superseded for <see cref="MaxAge"/>, or when the file has more than <see cref="MaxVersions"/> versions, whichever
/// comes first. The current version and "Keep forever" versions always stay. Admins can change the limits.
/// </summary>
public sealed record VersionRetention(TimeSpan MaxAge, int MaxVersions)
{
    public static readonly VersionRetention Default = new(TimeSpan.FromDays(30), 100);

    /// <summary>
    /// When each version becomes due for purging, keyed by version number; null means it is kept. A version's age
    /// counts from when the next one replaced it.
    /// </summary>
    public IReadOnlyDictionary<int, DateTimeOffset?> PurgeDates(IReadOnlyCollection<FileVersion> versions, Guid? currentVersionId, DateTimeOffset now)
    {
        var newestFirst = versions.OrderByDescending(v => v.Number).ToList();
        var dates = new Dictionary<int, DateTimeOffset?>();
        for (var i = 0; i < newestFirst.Count; i++)
        {
            var version = newestFirst[i];
            if (version.Id == currentVersionId || version.KeepForever)
            {
                dates[version.Number] = null;
                continue;
            }

            var supersededAt = i > 0 ? newestFirst[i - 1].UploadedAt : version.UploadedAt;
            var byAge = supersededAt + MaxAge;
            dates[version.Number] = i >= MaxVersions && now < byAge ? now : byAge;
        }

        return dates;
    }
}
