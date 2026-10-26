namespace Dokument.Domain.Previews;

/// <summary>The streams ffprobe found, as far as deciding playback goes.</summary>
/// <param name="FormatName">ffprobe's <c>format_name</c>, e.g. <c>mov,mp4,m4a,3gp,3g2,mj2</c> or <c>matroska,webm</c>.</param>
/// <param name="MajorBrand">The MP4 brand, e.g. <c>qt  </c> for a QuickTime .mov or <c>isom</c>.</param>
/// <param name="PixelFormat">The video's, e.g. <c>yuv420p</c>; 10-bit H.264 is <c>yuv420p10le</c> and few browsers play it.</param>
public sealed record ProbedStreams(
    string FormatName,
    string? MajorBrand,
    string? VideoCodec,
    string? PixelFormat,
    string? AudioCodec
);

/// <summary>
/// Decides whether browsers can play a file as it is (docs/design.md §14). Only what every current browser plays counts
/// as direct, so Firefox on Linux gets the same answer as Safari; browsers that can do more (HEVC in Safari) are
/// handled by the viewer, which tries the original first.
/// </summary>
public static class MediaPlanner
{
    private static readonly HashSet<string> WebVideo = ["h264", "vp8", "vp9", "av1"];
    private static readonly HashSet<string> WebPixels = ["yuv420p", "yuvj420p"];
    private static readonly HashSet<string> WebAudio = ["aac", "mp3", "opus", "vorbis", "flac"];

    /// <summary>Containers browsers play, and the audio codecs each may hold as audio-only files.</summary>
    private static readonly Dictionary<string, HashSet<string>> AudioFiles = new()
    {
        ["mp3"] = ["mp3"],
        ["ogg"] = ["opus", "vorbis", "flac"],
        ["flac"] = ["flac"],
        ["wav"] = ["pcm_s16le", "pcm_u8", "pcm_s24le", "pcm_f32le"],
        ["aac"] = ["aac"],
    };

    /// <summary>Formats that hold media at all. Anything else ffprobe can open (images, subtitles, text) isn't ours.</summary>
    private static readonly string[] MediaFormats =
    [
        "mov",
        "mp4",
        "matroska",
        "webm",
        "avi",
        "asf",
        "mpegts",
        "mpeg",
        "flv",
        "ogg",
        "mp3",
        "wav",
        "flac",
        "aac",
        "aiff",
        "amr",
        "ape",
        "wv",
        "caf",
        "3gp",
    ];

    public static bool IsMediaFormat(string formatName) =>
        formatName.Split(',').Any(name => MediaFormats.Contains(name));

    /// <summary>Video every browser decodes: H.264 only in 8-bit 4:2:0.</summary>
    public static bool IsWebVideo(string codec, string? pixelFormat) =>
        WebVideo.Contains(codec) && (pixelFormat is null || WebPixels.Contains(pixelFormat));

    public static bool IsWebAudio(string codec) => WebAudio.Contains(codec);

    public static PlaybackPlan Plan(ProbedStreams media)
    {
        var formats = media.FormatName.Split(',');
        var video = media.VideoCodec;
        var audio = media.AudioCodec;
        var webVideo = video is null || IsWebVideo(video, media.PixelFormat);
        var webAudio = audio is null || IsWebAudio(audio);
        if (!webVideo)
        {
            return PlaybackPlan.Transcode;
        }

        if (video is null)
        {
            // Audio only: a known pairing plays; AAC or ALAC in .m4a, WMA and the rest are copied.
            if (audio is null)
            {
                return PlaybackPlan.Transcode;
            }

            if (
                formats.Any(f =>
                    AudioFiles.TryGetValue(f, out var codecs) && codecs.Contains(audio)
                )
            )
            {
                return PlaybackPlan.Direct;
            }

            if (IsMp4(media) && audio is "aac" or "mp3" or "opus" or "flac")
            {
                return PlaybackPlan.Direct;
            }

            return webAudio ? PlaybackPlan.Remux : PlaybackPlan.Transcode;
        }

        if (!webAudio)
        {
            return PlaybackPlan.Transcode;
        }

        if (IsMp4(media))
        {
            return PlaybackPlan.Direct;
        }

        // WebM holds only VP8/VP9/AV1 with Opus or Vorbis; Matroska with those plays too.
        if (formats.Contains("webm") && video is not "h264" && audio is null or "opus" or "vorbis")
        {
            return PlaybackPlan.Direct;
        }

        // H.264 in .mov or .mkv, VP9 in .mkv with AAC and the like: the streams fit in MP4 as they are.
        return PlaybackPlan.Remux;
    }

    /// <summary>MP4 proper, not a QuickTime .mov: ffprobe names both "mov,mp4,…", and the brand tells them apart.</summary>
    private static bool IsMp4(ProbedStreams media) =>
        media.FormatName.Split(',').Contains("mp4") && media.MajorBrand?.Trim() is not "qt";
}
