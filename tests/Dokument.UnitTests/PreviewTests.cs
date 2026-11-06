using Dokument.Domain.Files;
using Dokument.Domain.Jobs;
using Dokument.Domain.Previews;
using Dokument.Tests;
using Dokument.Worker.Previews;
using NetVips;

namespace Dokument.UnitTests;

public sealed class ThumbnailerTests : IDisposable
{
    private readonly string dir = Directory.CreateTempSubdirectory("dokument-thumbs-").FullName;
    private readonly Thumbnailer thumbnailer = new();

    public void Dispose() => Directory.Delete(dir, recursive: true);

    private RenderResult Render(byte[] bytes)
    {
        var path = Path.Combine(dir, Guid.NewGuid().ToString("N"));
        File.WriteAllBytes(path, bytes);
        return thumbnailer.Render(path, TestContext.Current.CancellationToken);
    }

    private static Image Decode(RenderedImage image) => Image.NewFromBuffer(image.Bytes);

    [Fact]
    public void Draws_an_audio_files_waveform_as_its_tile_on_a_clear_ground()
    {
        var waveform = new Dokument.Worker.Media.Waveform();
        var samples = new short[Dokument.Worker.Media.Waveform.SampleRate * 4];
        for (var i = 0; i < samples.Length / 2; i++)
        {
            samples[i] = (short)(20000 * Math.Sin(i * 0.3) * Math.Abs(Math.Sin(i * 0.001)));
        }

        waveform.Add(samples);

        var result = thumbnailer.RenderWaveform(waveform, TestContext.Current.CancellationToken);

        Assert.Equal(PreviewStatus.Ready, result.Status);
        Assert.Equal((640, 480), (result.Images[1].Width, result.Images[1].Height));
        using var tile = Decode(result.Images[1]);
        Assert.Equal(4, tile.Bands);
        // The corner is see-through, the middle of a loud stretch isn't.
        Assert.Equal(0, tile.Getpoint(2, 2)[3]);
        Assert.Equal(255, tile.Getpoint(100, 240)[3]);
        Assert.Empty(
            thumbnailer
                .RenderWaveform(
                    new Dokument.Worker.Media.Waveform(),
                    TestContext.Current.CancellationToken
                )
                .Images
        );
    }

    [Fact]
    public void Makes_both_thumbnail_sizes_from_an_image_without_upscaling_or_a_preview()
    {
        var result = Render(TestFiles.Png(1600, 1200));

        Assert.Equal(PreviewStatus.Ready, result.Status);
        Assert.Equal((1600, 1200), (result.Width, result.Height));
        Assert.Equal(
            [RenditionKind.ThumbSmall, RenditionKind.ThumbMedium],
            result.Images.Select(i => i.Kind)
        );
        Assert.Equal((256, 192), (result.Images[0].Width, result.Images[0].Height));
        Assert.Equal((640, 480), (result.Images[1].Width, result.Images[1].Height));
        using var decoded = Decode(result.Images[1]);
        Assert.Equal("webpload_buffer", decoded.Get("vips-loader"));

        var small = Render(TestFiles.Png(100, 50));
        Assert.All(small.Images, i => Assert.Equal((100, 50), (i.Width, i.Height)));
    }

    [Fact]
    public void Applies_EXIF_rotation_to_the_renditions()
    {
        var result = Render(TestFiles.SidewaysJpeg(storedWidth: 800, storedHeight: 400));

        Assert.Equal((400, 800), (result.Width, result.Height));
        var medium = result.Images.Single(i => i.Kind == RenditionKind.ThumbMedium);
        Assert.Equal((320, 640), (medium.Width, medium.Height));
        using var decoded = Decode(medium);
        Assert.False(decoded.Contains("orientation") && (int)decoded.Get("orientation") != 1);
    }

    [Fact]
    public void Gives_huge_and_non_web_images_a_preview()
    {
        var huge = Render(TestFiles.Png(5000, 100));
        var preview = huge.Images.Single(i => i.Kind == RenditionKind.Preview);
        Assert.Equal(2048, preview.Width);

        using var tiff = TestFiles.Gradient(300, 200);
        var fromTiff = Render(tiff.TiffsaveBuffer());
        Assert.Contains(fromTiff.Images, i => i.Kind == RenditionKind.Preview && i.Width == 300);
    }

    [Fact]
    public void Renders_page_one_of_a_PDF_on_white()
    {
        var result = Render(TestFiles.Pdf(pages: 3));

        Assert.Equal(PreviewStatus.Ready, result.Status);
        Assert.Equal(3, result.PageCount);
        Assert.Equal((595, 842), (result.Width, result.Height));
        var medium = result.Images.Single(i => i.Kind == RenditionKind.ThumbMedium);
        Assert.Equal(640, medium.Height);
        using var decoded = Decode(medium);

        // The black box is in the top left; the rest of the page is white.
        Assert.True(decoded.Getpoint(decoded.Width / 5, decoded.Height / 10).All(v => v < 30));
        Assert.True(
            decoded.Getpoint(decoded.Width * 4 / 5, decoded.Height * 4 / 5).All(v => v > 225)
        );
    }

    [Fact]
    public void Leaves_other_files_to_the_type_icon()
    {
        Assert.Equal(
            PreviewStatus.Unsupported,
            Render("name,amount\nrent,9000\n"u8.ToArray()).Status
        );
        Assert.Equal(
            PreviewStatus.Unsupported,
            Render([0x50, 0x4B, 0x03, 0x04, 0, 0, 0, 0]).Status
        );
        Assert.Equal(PreviewStatus.Unsupported, Render([]).Status);
    }

    [Fact]
    public void Records_files_that_look_renderable_but_are_broken()
    {
        var truncatedPdf = TestFiles.Pdf()[..40];
        Assert.Equal(PreviewStatus.Failed, Render(truncatedPdf).Status);

        // Just the header: libvips knows it's a PNG, but there are no pixels. (A merely truncated image still renders, like a half-loaded photo.)
        var png = TestFiles.Png(400, 300);
        Assert.Equal(PreviewStatus.Failed, Render(png[..40]).Status);
    }
}

public class JobTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Retries_with_exponential_backoff_then_gives_up()
    {
        var retry = RetryPolicy.Default;
        Assert.Equal(Now.AddSeconds(30), retry.NextAttempt(1, Now));
        Assert.Equal(Now.AddSeconds(60), retry.NextAttempt(2, Now));
        Assert.Equal(Now.AddSeconds(240), retry.NextAttempt(4, Now));
        Assert.Null(retry.NextAttempt(5, Now));
        Assert.Equal(Now.AddHours(1), (retry with { MaxAttempts = 100 }).NextAttempt(20, Now));
    }

    [Fact]
    public void A_failed_job_is_queued_again_until_its_attempts_run_out()
    {
        var job = Job.Create(JobType.Thumbnail, "abc", JobPriority.Interactive, Now);
        job.Fail(
            new string('x', 5000),
            new RetryPolicy(1, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1)),
            Now
        );

        // Attempts is counted by the claim; with none recorded, one attempt is still allowed.
        Assert.Equal(JobStatus.Queued, job.Status);
        Assert.Equal(Job.MaxErrorLength, job.LastError!.Length);

        var spent = Job.Create(JobType.Thumbnail, "abc", JobPriority.Interactive, Now);
        spent.Fail(
            "boom",
            new RetryPolicy(0, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1)),
            Now
        );
        Assert.Equal(JobStatus.Failed, spent.Status);
        Assert.Equal(Now, spent.FinishedAt);

        spent.Requeue(Now);
        Assert.Equal(JobStatus.Queued, spent.Status);
        Assert.Equal(0, spent.Attempts);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Thumbnail")]
    [InlineData("thumb nail")]
    public void Job_type_names_are_lowercase_slugs(string name) =>
        Assert.Throws<ArgumentException>(() => JobType.From(name));

    [Fact]
    public void Renditions_live_under_their_blob_hash()
    {
        Assert.True(ContentHash.TryParse(new string('a', 62) + "bc", out var hash));
        var key = DerivedKey.For(hash, RenditionKind.ThumbMedium);
        Assert.Equal($"thumbs/aa/{hash.Hex}/thumb_m.webp", key.Value);
        Assert.Equal(key, DerivedKey.From(key.Value));
        Assert.Throws<ArgumentException>(() => DerivedKey.From("thumbs/../../originals/blobs/x"));
        Assert.All(
            Enum.GetValues<RenditionKind>(),
            k => Assert.Equal(k, RenditionKinds.FromKey(k.Key()))
        );
    }
}
