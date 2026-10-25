namespace Dokument.Domain.Search;

/// <summary>
/// The file groups search filters on (docs/design.md §13.2), the same groups the clients give their own icon and colour
/// (§16.2). Each is a set of MIME type patterns in SQL <c>LIKE</c> form.
/// </summary>
public sealed record FileType
{
    public static readonly FileType Folder = new("folder", []);
    public static readonly FileType Pdf = new("pdf", ["application/pdf"]);
    public static readonly FileType Doc = new(
        "doc",
        [
            "application/msword",
            "application/vnd.openxmlformats-officedocument.wordprocessingml.%",
            "application/vnd.oasis.opendocument.text%",
            "application/rtf",
            "text/rtf",
        ]
    );
    public static readonly FileType Sheet = new(
        "sheet",
        [
            "application/vnd.ms-excel%",
            "application/vnd.openxmlformats-officedocument.spreadsheetml.%",
            "application/vnd.oasis.opendocument.spreadsheet%",
            "text/csv",
        ]
    );
    public static readonly FileType Slides = new(
        "slides",
        [
            "application/vnd.ms-powerpoint%",
            "application/vnd.openxmlformats-officedocument.presentationml.%",
            "application/vnd.oasis.opendocument.presentation%",
        ]
    );
    public static readonly FileType Image = new("image", ["image/%"]);
    public static readonly FileType Video = new("video", ["video/%"]);
    public static readonly FileType Audio = new("audio", ["audio/%"]);
    public static readonly FileType Archive = new(
        "archive",
        [
            "application/zip",
            "application/x-zip-compressed",
            "application/x-7z-compressed",
            "application/x-rar-compressed",
            "application/vnd.rar",
            "application/x-tar",
            "application/gzip",
            "application/x-gzip",
        ]
    );
    public static readonly FileType Text = new(
        "text",
        ["text/plain", "text/markdown", "application/json", "application/xml", "text/xml"]
    );

    public static readonly IReadOnlyList<FileType> All =
    [
        Folder,
        Pdf,
        Doc,
        Sheet,
        Slides,
        Image,
        Video,
        Audio,
        Archive,
        Text,
    ];

    private FileType(string name, string[] mimePatterns)
    {
        Name = name;
        MimePatterns = mimePatterns;
    }

    /// <summary>As in <c>type:pdf</c>.</summary>
    public string Name { get; }

    /// <summary>Empty for folders, which have no MIME type.</summary>
    public IReadOnlyList<string> MimePatterns { get; }

    public static FileType? TryParse(string? name) =>
        All.FirstOrDefault(t =>
            string.Equals(t.Name, name?.Trim(), StringComparison.OrdinalIgnoreCase)
        );

    public override string ToString() => Name;
}
