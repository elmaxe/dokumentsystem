namespace Dokument.Infrastructure.Storage;

/// <summary>Mount points inside the container (docs/design.md §7).</summary>
public sealed class StorageOptions
{
    public const string SectionName = "Storage";

    /// <summary>Precious: content-addressed originals and in-flight uploads.</summary>
    public string OriginalsPath { get; set; } = "/data/originals";

    /// <summary>Regenerable: thumbnails and preview renditions.</summary>
    public string DerivedPath { get; set; } = "/data/derived";

    /// <summary>Database dumps.</summary>
    public string BackupsPath { get; set; } = "/data/backups";

    public IEnumerable<string> All => [OriginalsPath, DerivedPath, BackupsPath];
}
