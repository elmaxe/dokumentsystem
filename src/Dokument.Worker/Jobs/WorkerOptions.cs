namespace Dokument.Worker.Jobs;

/// <summary>The <c>Worker</c> configuration section. Moves to the admin UI with the queue page (docs/design.md §10.1).</summary>
public sealed class WorkerOptions
{
    public const string SectionName = "Worker";

    /// <summary>Thumbnail jobs run at once. Each holds one decoded image in memory.</summary>
    public int ThumbnailConcurrency { get; set; } = 2;

    /// <summary>Metadata jobs run at once. Each runs one exiftool process.</summary>
    public int MetadataConcurrency { get; set; } = 2;

    /// <summary>Text jobs run at once. Recognizing a scan keeps a core busy, so one by default (§10.1).</summary>
    public int TextConcurrency { get; set; } = 1;

    /// <summary>Tesseract's language packs, joined with "+" (§23). The image ships Swedish and English.</summary>
    public string OcrLanguages { get; set; } = "swe+eng";

    /// <summary>
    /// The Tesseract command: a name on PATH, or a full path. The Windows installer doesn't add itself to PATH, so a
    /// worker run outside the image may need <c>C:\Program Files\Tesseract-OCR\tesseract.exe</c> here.
    /// </summary>
    public string TesseractCommand { get; set; } = "tesseract";

    /// <summary>The exiftool command: a name on PATH, or a full path.</summary>
    public string ExifToolCommand { get; set; } = "exiftool";

    /// <summary>Playable copies made at once. Re-encoding video keeps every core busy, so one (§10.1).</summary>
    public int TranscodeConcurrency { get; set; } = 1;

    /// <summary>The ffmpeg command: a name on PATH, or a full path.</summary>
    public string FfmpegCommand { get; set; } = "ffmpeg";

    /// <summary>The ffprobe command: a name on PATH, or a full path.</summary>
    public string FfprobeCommand { get; set; } = "ffprobe";

    /// <summary>Re-encoding a long video on a NAS takes a while; copy jobs get this long.</summary>
    public TimeSpan TranscodeJobTimeout { get; set; } = TimeSpan.FromHours(3);

    /// <summary>
    /// Transcriptions run at once (§11.1). The ML service does the work; one keeps a NAS's CPU or a GPU's memory free
    /// for everything else.
    /// </summary>
    public int TranscribeConcurrency { get; set; } = 1;

    /// <summary>A long recording on a CPU takes hours; the lease is renewed as it goes, so a dead worker lets go sooner.</summary>
    public TimeSpan TranscribeJobTimeout { get; set; } = TimeSpan.FromHours(12);

    /// <summary>A long scanned PDF takes a while; text jobs get this long instead of <see cref="JobTimeout"/>.</summary>
    public TimeSpan TextJobTimeout { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>How often an idle worker looks for new jobs.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>A job running longer than this is cancelled and counts as a failed attempt (§10.4).</summary>
    public TimeSpan JobTimeout { get; set; } = TimeSpan.FromMinutes(5);
}
