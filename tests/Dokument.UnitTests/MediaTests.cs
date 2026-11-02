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
public sealed class SeekPreviewLayoutTests
{
    [Theory]
    [InlineData(null, 0, 0)]
    [InlineData(8.0, 0, 0)]
    [InlineData(10.0, 5, 2000)]
    [InlineData(31.0, 15, 2000)]
    [InlineData(120.0, 60, 2000)]
    [InlineData(240.0, 100, 2400)]
    [InlineData(7200.0, 100, 72000)]
    public void A_frame_every_percent_but_at_least_two_seconds_apart(
        double? seconds,
        int count,
        int intervalMs
    )
    {
        var layout = SeekPreviewLayout.For(seconds);
        Assert.Equal(count, layout?.Count ?? 0);
        Assert.Equal(intervalMs, (int)Math.Round((layout?.IntervalSeconds ?? 0) * 1000));
    }

    [Fact]
    public void Frames_are_from_the_middle_of_their_stretch_and_fill_rows_of_ten()
    {
        var layout = SeekPreviewLayout.For(240)!;
        Assert.Equal(TimeSpan.FromSeconds(1.2), layout.TimeOf(0));
        Assert.Equal(TimeSpan.FromSeconds(238.8), layout.TimeOf(99));
        Assert.Equal((10, 10), (layout.Columns, layout.Rows));

        var clip = SeekPreviewLayout.For(30)!;
        Assert.Equal((10, 2), (clip.Columns, clip.Rows));
        var short_ = SeekPreviewLayout.For(12)!;
        Assert.Equal((6, 1), (short_.Columns, short_.Rows));
    }

    [Theory]
    [InlineData(1920, 1080, 256, 144)]
    [InlineData(1080, 1920, 144, 256)]
    [InlineData(1440, 1080, 256, 192)]
    [InlineData(160, 120, 160, 120)]
    [InlineData(null, null, 256, 144)]
    public void Frames_are_256_on_the_long_edge_and_never_scaled_up(
        int? width,
        int? height,
        int frameWidth,
        int frameHeight
    ) => Assert.Equal((frameWidth, frameHeight), SeekPreviewLayout.FrameSize(width, height));

    [Fact]
    public void A_sheet_that_doesnt_fit_the_length_isnt_offered()
    {
        Assert.Equal(
            new SeekPreviewSheet(2400, 100, 10, 256, 144),
            SeekPreviewSheet.From(240, 2560, 1440)
        );
        Assert.Null(SeekPreviewSheet.From(240, 2560, 1441));
        Assert.Null(SeekPreviewSheet.From(5, 2560, 1440));
    }

    [Fact]
    public void Missing_frames_borrow_their_neighbours()
    {
        byte[] a = [1],
            b = [2];
        byte[]?[] frames = [null, a, null, b, null];
        Assert.True(SeekPreviewsHandler.Fill(frames));
        Assert.Equal([a, a, a, b, b], frames);
        Assert.False(SeekPreviewsHandler.Fill(new byte[]?[3]));
    }
}

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
    public async Task Seek_previews_are_upright_frames_of_one_size_joined_ten_to_a_row()
    {
        var ct = TestContext.Current.CancellationToken;
        var path = Path.Combine(dir, "clip.mp4");
        await File.WriteAllBytesAsync(
            path,
            Dokument.Tests.TestFiles.Video(
                "clip.mp4",
                seconds: 24,
                size: "320x180",
                "-c:v",
                "libx264",
                "-pix_fmt",
                "yuv420p",
                "-c:a",
                "aac"
            ),
            ct
        );
        var layout = SeekPreviewLayout.For(24)!;
        Assert.Equal(12, layout.Count);
        var (width, height) = SeekPreviewLayout.FrameSize(320, 180);

        var frames = new byte[]?[layout.Count];
        for (var i = 0; i < layout.Count; i++)
        {
            frames[i] = await ffmpeg.SeekFrameAsync(path, layout.TimeOf(i), width, height, ct);
            Assert.NotNull(frames[i]);
        }

        // Past the end there's nothing to take.
        Assert.Null(await ffmpeg.SeekFrameAsync(path, TimeSpan.FromSeconds(60), width, height, ct));

        var sheet = SeekPreviewsHandler.Join(frames!, layout.Columns);
        using var image = Image.NewFromBuffer(sheet);
        Assert.Equal("webpload_buffer", image.Get("vips-loader"));
        Assert.Equal((10 * 256, 2 * 144), (image.Width, image.Height));
        Assert.Equal(
            new SeekPreviewSheet(2000, 12, 10, 256, 144),
            SeekPreviewSheet.From(24, image.Width, image.Height)
        );

        // The test pattern's counter: frames a second apart differ, so each is its own seek.
        using var first = image.ExtractArea(0, 0, 256, 144);
        using var second = image.ExtractArea(256, 0, 256, 144);
        using var difference = (first - second).Abs();
        Assert.True(difference.Max() > 50);
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
