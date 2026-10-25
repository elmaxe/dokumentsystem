using System.Text;
using Dokument.Domain.Files;
using Dokument.Domain.Previews;
using Dokument.Domain.Settings;
using Dokument.Worker.Jobs;
using Dokument.Worker.Media;
using Microsoft.Extensions.Options;
using NetVips;

namespace Dokument.UnitTests;

public sealed class MediaPlannerTests
{
    private const string Mp4 = "mov,mp4,m4a,3gp,3g2,mj2";

    [Theory]
    // Phones and cameras: H.264 in MP4 plays as it is, in a .mov it only needs repackaging.
    [InlineData(Mp4, "isom", "h264", "yuv420p", "aac", PlaybackPlan.Direct)]
    [InlineData(Mp4, "qt  ", "h264", "yuv420p", "aac", PlaybackPlan.Remux)]
    [InlineData(Mp4, "qt  ", "hevc", "yuv420p10le", "aac", PlaybackPlan.Transcode)]
    [InlineData(Mp4, "isom", "h264", "yuv420p10le", "aac", PlaybackPlan.Transcode)]
    [InlineData(Mp4, "isom", "h264", "yuv420p", "pcm_s16le", PlaybackPlan.Transcode)]
    [InlineData("matroska,webm", null, "vp9", "yuv420p", "opus", PlaybackPlan.Direct)]
    [InlineData("matroska,webm", null, "h264", "yuv420p", "aac", PlaybackPlan.Remux)]
    [InlineData("matroska,webm", null, "h264", "yuv420p", "ac3", PlaybackPlan.Transcode)]
    [InlineData("avi", null, "mpeg4", "yuv420p", "mp3", PlaybackPlan.Transcode)]
    [InlineData("mpegts", null, "h264", "yuv420p", "aac", PlaybackPlan.Remux)]
    // Audio only.
    [InlineData("mp3", null, null, null, "mp3", PlaybackPlan.Direct)]
    [InlineData("ogg", null, null, null, "opus", PlaybackPlan.Direct)]
    [InlineData("flac", null, null, null, "flac", PlaybackPlan.Direct)]
    [InlineData("wav", null, null, null, "pcm_s16le", PlaybackPlan.Direct)]
    [InlineData(Mp4, "M4A ", null, null, "aac", PlaybackPlan.Direct)]
    [InlineData(Mp4, "M4A ", null, null, "alac", PlaybackPlan.Transcode)]
    [InlineData("asf", null, null, null, "wmav2", PlaybackPlan.Transcode)]
    [InlineData("matroska,webm", null, null, null, "aac", PlaybackPlan.Remux)]
    public void Plans_playback_from_the_streams(
        string format,
        string? brand,
        string? video,
        string? pixels,
        string? audio,
        PlaybackPlan expected
    ) =>
        Assert.Equal(
            expected,
            MediaPlanner.Plan(new ProbedStreams(format, brand, video, pixels, audio))
        );

    [Theory]
    [InlineData("mov,mp4,m4a,3gp,3g2,mj2", true)]
    [InlineData("matroska,webm", true)]
    [InlineData("mp3", true)]
    [InlineData("image2", false)]
    [InlineData("png_pipe", false)]
    [InlineData("srt", false)]
    public void Knows_media_formats_from_images_and_subtitles(string format, bool expected) =>
        Assert.Equal(expected, MediaPlanner.IsMediaFormat(format));
}

public sealed class MediaProbeTests
{
    private static MediaProbe Parse(string json) => FFmpeg.Parse(Encoding.UTF8.GetBytes(json))!;

    [Fact]
    public void Reads_a_phone_video_turned_upright_by_its_display_matrix()
    {
        var probe = Parse(
            """
            {
              "streams": [
                { "index": 0, "codec_type": "video", "codec_name": "hevc", "pix_fmt": "yuv420p10le",
                  "color_transfer": "arib-std-b67", "width": 3840, "height": 2160,
                  "side_data_list": [ { "side_data_type": "Display Matrix", "rotation": -90 } ],
                  "disposition": { "attached_pic": 0 } },
                { "index": 1, "codec_type": "audio", "codec_name": "aac" },
                { "index": 2, "codec_type": "data", "codec_name": "none" }
              ],
              "format": { "format_name": "mov,mp4,m4a,3gp,3g2,mj2", "duration": "12.345000",
                          "tags": { "major_brand": "qt  " } }
            }
            """
        );

        Assert.Equal(12.345, probe.DurationSeconds);
        Assert.Equal("qt  ", probe.MajorBrand);
        Assert.Equal("hevc", probe.Video?.Codec);
        Assert.Equal("aac", probe.Audio?.Codec);
        Assert.True(probe.IsHdr);
        Assert.Equal((2160, 3840), probe.DisplaySize);
        Assert.Equal(PlaybackPlan.Transcode, MediaPlanner.Plan(probe.ForPlanning));
    }

    [Fact]
    public void Tells_cover_art_from_video_and_reads_the_old_rotate_tag()
    {
        var song = Parse(
            """
            {
              "streams": [
                { "index": 0, "codec_type": "audio", "codec_name": "mp3" },
                { "index": 1, "codec_type": "video", "codec_name": "mjpeg", "width": 600, "height": 600,
                  "disposition": { "attached_pic": 1 } }
              ],
              "format": { "format_name": "mp3", "duration": "N/A" }
            }
            """
        );
        var old = Parse(
            """
            {
              "streams": [ { "index": 0, "codec_type": "video", "codec_name": "h264", "width": 640, "height": 480,
                             "tags": { "rotate": "90" } } ],
              "format": { "format_name": "mov,mp4,m4a,3gp,3g2,mj2" }
            }
            """
        );

        Assert.Null(song.Video);
        Assert.Equal(1, song.Cover?.Index);
        Assert.Null(song.DurationSeconds);
        Assert.Equal(PlaybackPlan.Direct, MediaPlanner.Plan(song.ForPlanning));
        Assert.Equal((480, 640), old.DisplaySize);
    }

    [Fact]
    public void Copies_streams_that_play_and_converts_the_rest()
    {
        var mov = Parse(
            """
            { "streams": [ { "index": 0, "codec_type": "video", "codec_name": "h264", "pix_fmt": "yuv420p" },
                           { "index": 1, "codec_type": "audio", "codec_name": "pcm_s16le" } ],
              "format": { "format_name": "mov,mp4,m4a,3gp,3g2,mj2" } }
            """
        );
        var hevc = Parse(
            """
            { "streams": [ { "index": 0, "codec_type": "video", "codec_name": "hevc", "pix_fmt": "yuv420p10le",
                             "color_transfer": "smpte2084" } ],
              "format": { "format_name": "matroska,webm" } }
            """
        );

        var remux = string.Join(
            ' ',
            TranscodeHandler.CopyArguments(mov, "in", "out", 1920, toneMap: false)
        );
        var transcode = string.Join(
            ' ',
            TranscodeHandler.CopyArguments(hevc, "in", "out", 1920, toneMap: true)
        );

        Assert.Contains("-map 0:V:0 -c:v copy", remux, StringComparison.Ordinal);
        Assert.Contains("-map 0:a:0 -c:a aac -b:a 160k -ac 2", remux, StringComparison.Ordinal);
        Assert.EndsWith("-movflags +faststart -f mp4 -y out", remux, StringComparison.Ordinal);
        Assert.Contains("-c:v libx264", transcode, StringComparison.Ordinal);
        Assert.Contains("min(iw,1920)", transcode, StringComparison.Ordinal);
        Assert.Contains("tonemap=tonemap=hable", transcode, StringComparison.Ordinal);
        Assert.DoesNotContain("0:a:0", transcode, StringComparison.Ordinal);
    }

    [Fact]
    public void Keeps_copies_in_their_own_folder_beside_the_thumbnails()
    {
        var hash = ContentHash.FromBytes(System.Security.Cryptography.SHA256.HashData([1, 2, 3]));
        var key = DerivedKey.For(hash, RenditionKind.Playable);

        Assert.Equal($"previews/{hash.Hex[..2]}/{hash.Hex}/playable.mp4", key.Value);
        Assert.Equal(key, DerivedKey.From(key.Value));
        Assert.Throws<ArgumentException>(() => DerivedKey.From("previews/../etc/passwd"));
    }

    [Theory]
    [InlineData(720, 1280)]
    [InlineData(1080, 1920)]
    [InlineData(0, null)]
    public void Fits_copies_in_a_square_so_portrait_videos_keep_their_size(
        int maxHeight,
        int? edge
    ) => Assert.Equal(edge, new VideoSettings(VideoCopies.Background, maxHeight).MaxEdge);
}

/// <summary>Against the real ffmpeg, which CI installs. Videos are made with its test pattern, so the repo holds none.</summary>
public sealed class FFmpegTests : IDisposable
{
    private readonly string dir = Directory.CreateTempSubdirectory("dokument-media-").FullName;
    private readonly FFmpeg ffmpeg = new(Options.Create(new WorkerOptions()));

    public void Dispose() => Directory.Delete(dir, recursive: true);

    private string Make(string name, params string[] encode)
    {
        var path = Path.Combine(dir, name);
        File.WriteAllBytes(path, Dokument.Tests.TestFiles.Video(name, encode));
        return path;
    }

    [Fact]
    public async Task Converts_a_video_browsers_cant_play_into_mp4_that_they_can()
    {
        var ct = TestContext.Current.CancellationToken;
        var avi = Make("old.avi", "-c:v", "mpeg4", "-c:a", "pcm_s16le");

        var probe = await ffmpeg.ProbeAsync(avi, ct);
        Assert.NotNull(probe);
        Assert.Equal(PlaybackPlan.Transcode, MediaPlanner.Plan(probe.ForPlanning));
        Assert.Equal((320, 240), probe.DisplaySize);
        Assert.InRange(probe.DurationSeconds ?? 0, 1.9, 2.1);

        var poster = await ffmpeg.FrameAsync(avi, TimeSpan.FromSeconds(0.2), ct);
        Assert.NotNull(poster);
        using (var image = Image.NewFromBuffer(poster))
        {
            Assert.Equal((320, 240), (image.Width, image.Height));
        }

        var copy = Path.Combine(dir, "copy.mp4");
        var progress = new List<TimeSpan>();
        var (exitCode, error) = await ffmpeg.RunAsync(
            TranscodeHandler.CopyArguments(probe, avi, copy, 1920, toneMap: false),
            TimeSpan.FromMinutes(1),
            progress.Add,
            ct
        );
        Assert.True(exitCode == 0, error);
        Assert.NotEmpty(progress);

        var made = await ffmpeg.ProbeAsync(copy, ct);
        Assert.NotNull(made);
        Assert.Equal("h264", made.Video?.Codec);
        Assert.Equal("aac", made.Audio?.Codec);
        Assert.Equal(PlaybackPlan.Direct, MediaPlanner.Plan(made.ForPlanning));
    }

    [Fact]
    public async Task Says_nothing_about_files_that_arent_media()
    {
        var ct = TestContext.Current.CancellationToken;
        var text = Path.Combine(dir, "notes.txt");
        await File.WriteAllTextAsync(text, "Not a video.", ct);
        var png = Path.Combine(dir, "photo.png");
        await File.WriteAllBytesAsync(png, Dokument.Tests.TestFiles.Png(64, 48), ct);

        Assert.Null(await ffmpeg.ProbeAsync(text, ct));
        Assert.Null(await ffmpeg.ProbeAsync(png, ct));
    }
}

public sealed class OfficeFormatsTests
{
    [Theory]
    [InlineData("application/vnd.openxmlformats-officedocument.wordprocessingml.document", "docx")]
    [InlineData("application/vnd.ms-excel", "xls")]
    [InlineData("application/vnd.oasis.opendocument.presentation", "odp")]
    [InlineData("text/rtf; charset=utf-8", "rtf")]
    [InlineData("application/pdf", null)]
    [InlineData("text/plain", null)]
    [InlineData(null, null)]
    public void Knows_the_formats_Gotenberg_converts(string? mimeType, string? extension) =>
        Assert.Equal(extension, OfficeFormats.ExtensionFor(mimeType));

    [Fact]
    public void Checks_the_bytes_so_a_renamed_file_never_reaches_LibreOffice()
    {
        Assert.True(OfficeFormats.LooksLike("docx", "PK\x03\x04"u8));
        Assert.True(
            OfficeFormats.LooksLike("doc", [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1])
        );
        Assert.True(OfficeFormats.LooksLike("rtf", "{\\rtf1\\ansi"u8));
        Assert.False(OfficeFormats.LooksLike("docx", "Hello"u8));
        Assert.False(OfficeFormats.LooksLike("xls", "PK\x03\x04"u8));
    }
}
