namespace Dokument.Domain.Previews;

/// <summary>
/// Where the frames of a video's seek-preview sheet are (docs/design.md §14): one frame for every 1 % of the video,
/// but never closer than 2 seconds apart, so a short clip gets fewer frames. Each frame stands for its stretch of the
/// timeline and is taken from the middle of it. Frames are laid out at most 10 to a row, left to right, top to bottom.
/// Videos shorter than 10 seconds get none.
/// </summary>
public sealed record SeekPreviewLayout(double IntervalSeconds, int Count)
{
    public const int MaxColumns = 10;
    public const int MaxCount = 100;

    /// <summary>The longest edge of one frame, in pixels. Shown at 192 or 144 CSS px, so it stays sharp on dense screens.</summary>
    public const int FrameEdge = 256;

    public static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan MinDuration = TimeSpan.FromSeconds(10);

    /// <summary>10, or fewer for a clip with fewer frames.</summary>
    public int Columns => Math.Min(MaxColumns, Count);

    public int Rows => (Count + Columns - 1) / Columns;

    /// <summary>When the frame at <paramref name="index"/> is taken: the middle of its stretch.</summary>
    public TimeSpan TimeOf(int index) => TimeSpan.FromSeconds((index + 0.5) * IntervalSeconds);

    /// <summary>Null when the video is too short for seek previews, or its length isn't known.</summary>
    public static SeekPreviewLayout? For(double? durationSeconds)
    {
        if (durationSeconds is not { } duration || duration < MinDuration.TotalSeconds)
        {
            return null;
        }

        var interval = Math.Max(duration / MaxCount, MinInterval.TotalSeconds);
        var count = Math.Clamp((int)Math.Floor(duration / interval), 1, MaxCount);
        return new SeekPreviewLayout(interval, count);
    }

    /// <summary>
    /// The size of one frame for a picture of <paramref name="width"/> × <paramref name="height"/> as shown: 256 px on
    /// the long edge, never scaled up, and even, which ffmpeg's scaler likes.
    /// </summary>
    public static (int Width, int Height) FrameSize(int? width, int? height)
    {
        if (width is not > 0 || height is not > 0)
        {
            return (FrameEdge, FrameEdge * 9 / 16);
        }

        var scale = Math.Min(1.0, (double)FrameEdge / Math.Max(width.Value, height.Value));
        static int Even(double value) => Math.Max(2, (int)Math.Round(value / 2) * 2);
        return (Even(width.Value * scale), Even(height.Value * scale));
    }
}

/// <summary>A video's seek-preview sheet, as the API describes it: how often a frame was taken, how many, and their size.</summary>
public sealed record SeekPreviewSheet(
    int IntervalMs,
    int Count,
    int Columns,
    int FrameWidth,
    int FrameHeight
)
{
    /// <summary>
    /// From the sheet's rendition and the media's length; null when they don't agree (a sheet made for another length
    /// would show the wrong frames).
    /// </summary>
    public static SeekPreviewSheet? From(double? durationSeconds, int sheetWidth, int sheetHeight)
    {
        var layout = SeekPreviewLayout.For(durationSeconds);
        if (layout is null || sheetWidth % layout.Columns != 0 || sheetHeight % layout.Rows != 0)
        {
            return null;
        }

        return new SeekPreviewSheet(
            (int)Math.Round(layout.IntervalSeconds * 1000),
            layout.Count,
            layout.Columns,
            sheetWidth / layout.Columns,
            sheetHeight / layout.Rows
        );
    }
}
