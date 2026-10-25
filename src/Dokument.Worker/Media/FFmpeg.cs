using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Dokument.Domain.Previews;
using Dokument.Worker.Jobs;
using Dokument.Worker.Search;
using Microsoft.Extensions.Options;

namespace Dokument.Worker.Media;

/// <summary>One stream ffprobe found.</summary>
public sealed record ProbedStream(
    int Index,
    string Type,
    string? Codec,
    string? PixelFormat,
    string? ColorTransfer,
    int? Width,
    int? Height,
    int Rotation,
    bool AttachedPicture
);

/// <summary>What ffprobe said about a file.</summary>
public sealed record MediaProbe(
    string FormatName,
    string? MajorBrand,
    double? DurationSeconds,
    IReadOnlyList<ProbedStream> Streams
)
{
    /// <summary>The first real video stream, not cover art.</summary>
    public ProbedStream? Video =>
        Streams.FirstOrDefault(s => s is { Type: "video", AttachedPicture: false });
    public ProbedStream? Audio => Streams.FirstOrDefault(s => s.Type == "audio");
    public ProbedStream? Cover =>
        Streams.FirstOrDefault(s => s is { Type: "video", AttachedPicture: true });

    public ProbedStreams ForPlanning =>
        new(FormatName, MajorBrand, Video?.Codec, Video?.PixelFormat, Audio?.Codec);

    /// <summary>HDR video (PQ or HLG), which needs tone mapping to look right as an 8-bit copy.</summary>
    public bool IsHdr => Video?.ColorTransfer is "smpte2084" or "arib-std-b67";

    /// <summary>The picture as shown: a phone video recorded upright is stored sideways with a rotation.</summary>
    public (int Width, int Height)? DisplaySize =>
        Video is { Width: { } w, Height: { } h } video
            ? Math.Abs(video.Rotation) % 180 == 90
                ? (h, w)
                : (w, h)
            : null;
}

/// <summary>
/// Runs ffprobe and ffmpeg (docs/design.md §10.3), which ship in the image. Arguments go through
/// <see cref="ProcessStartInfo.ArgumentList"/>, never a shell; the process is killed when the job is cancelled or runs
/// past its time. Other stages that need audio or frames (transcription) use the same runner.
/// </summary>
public sealed class FFmpeg(IOptions<WorkerOptions> options)
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan FrameTimeout = TimeSpan.FromMinutes(2);

    /// <summary>Probes <paramref name="path"/>. Null when ffprobe can't read it or it isn't audio or video.</summary>
    public async Task<MediaProbe?> ProbeAsync(string path, CancellationToken ct)
    {
        var output = await ExternalTool.RunAsync(
            options.Value.FfprobeCommand,
            ["-v", "error", "-print_format", "json", "-show_format", "-show_streams", path],
            ProbeTimeout,
            ct
        );
        if (output.ExitCode != 0)
        {
            return null;
        }

        try
        {
            var probe = Parse(output.Stdout);
            return
                probe is not null
                && MediaPlanner.IsMediaFormat(probe.FormatName)
                && (probe.Video is not null || probe.Audio is not null)
                ? probe
                : null;
        }
        catch (Exception e)
            when (e is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return null;
        }
    }

    internal static MediaProbe? Parse(byte[] json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (!root.TryGetProperty("format", out var format))
        {
            return null;
        }

        var streams = new List<ProbedStream>();
        if (root.TryGetProperty("streams", out var list))
        {
            foreach (var s in list.EnumerateArray())
            {
                streams.Add(
                    new ProbedStream(
                        s.GetProperty("index").GetInt32(),
                        Text(s, "codec_type") ?? "",
                        Text(s, "codec_name"),
                        Text(s, "pix_fmt"),
                        Text(s, "color_transfer"),
                        Number(s, "width"),
                        Number(s, "height"),
                        RotationOf(s),
                        s.TryGetProperty("disposition", out var d)
                            && d.TryGetProperty("attached_pic", out var pic)
                            && pic.ValueKind == JsonValueKind.Number
                            && pic.GetInt32() == 1
                    )
                );
            }
        }

        var duration =
            Text(format, "duration") is { } text
            && double.TryParse(
                text,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var seconds
            )
            && seconds > 0
                ? seconds
                : (double?)null;
        var brand =
            format.TryGetProperty("tags", out var tags) && Text(tags, "major_brand") is { } b
                ? b
                : null;
        return new MediaProbe(Text(format, "format_name") ?? "", brand, duration, streams);
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? Number(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetInt32()
            : null;

    /// <summary>From the display matrix (new files) or the old <c>rotate</c> tag.</summary>
    private static int RotationOf(JsonElement stream)
    {
        if (stream.TryGetProperty("side_data_list", out var side))
        {
            foreach (var data in side.EnumerateArray())
            {
                if (
                    data.TryGetProperty("rotation", out var rotation)
                    && rotation.ValueKind == JsonValueKind.Number
                )
                {
                    return (int)rotation.GetDouble();
                }
            }
        }

        return
            stream.TryGetProperty("tags", out var tags)
            && Text(tags, "rotate") is { } rotate
            && int.TryParse(rotate, CultureInfo.InvariantCulture, out var degrees)
            ? degrees
            : 0;
    }

    /// <summary>
    /// A still for the poster: the most representative of the frames from <paramref name="at"/> on, as PNG bytes,
    /// turned upright. Null when there are no frames there.
    /// </summary>
    public async Task<byte[]?> FrameAsync(string path, TimeSpan at, CancellationToken ct)
    {
        var output = await ExternalTool.RunAsync(
            options.Value.FfmpegCommand,
            [
                "-nostdin",
                "-v",
                "error",
                "-ss",
                at.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture),
                "-i",
                path,
                "-map",
                "0:V:0",
                // The most typical of 60 frames, so a fade or a blink isn't the poster.
                "-vf",
                "thumbnail=60",
                "-frames:v",
                "1",
                "-c:v",
                "png",
                "-f",
                "image2pipe",
                "-",
            ],
            FrameTimeout,
            ct
        );
        return output.ExitCode == 0 && output.Stdout.Length > 0 ? output.Stdout : null;
    }

    /// <summary>The embedded cover art of an audio file (or a video's attached picture), as PNG bytes.</summary>
    public async Task<byte[]?> CoverAsync(string path, int streamIndex, CancellationToken ct)
    {
        var output = await ExternalTool.RunAsync(
            options.Value.FfmpegCommand,
            [
                "-nostdin",
                "-v",
                "error",
                "-i",
                path,
                "-map",
                $"0:{streamIndex}",
                "-frames:v",
                "1",
                "-c:v",
                "png",
                "-f",
                "image2pipe",
                "-",
            ],
            FrameTimeout,
            ct
        );
        return output.ExitCode == 0 && output.Stdout.Length > 0 ? output.Stdout : null;
    }

    /// <summary>
    /// Runs ffmpeg with <paramref name="arguments"/> plus <c>-progress</c>, calling <paramref name="progress"/> with how
    /// much of the input it has written. Returns the exit code and the last of what it printed on stderr.
    /// </summary>
    public async Task<(int ExitCode, string Error)> RunAsync(
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        Action<TimeSpan>? progress,
        CancellationToken ct
    )
    {
        var start = new ProcessStartInfo(options.Value.FfmpegCommand)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (
            var argument in (string[])["-nostdin", "-v", "error", "-nostats", "-progress", "pipe:1"]
        )
        {
            start.ArgumentList.Add(argument);
        }

        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = start };
        try
        {
            process.Start();
        }
        catch (Win32Exception e)
        {
            throw new ToolMissingException(start.FileName, e);
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);
        try
        {
            var stderr = ReadTailAsync(process.StandardError, deadline.Token);
            while (await process.StandardOutput.ReadLineAsync(deadline.Token) is { } line)
            {
                // "out_time_us=12345678"; N/A until the first frame is written.
                if (
                    progress is not null
                    && line.StartsWith("out_time_us=", StringComparison.Ordinal)
                    && long.TryParse(line.AsSpan(12), CultureInfo.InvariantCulture, out var micros)
                    && micros >= 0
                )
                {
                    progress(TimeSpan.FromMicroseconds(micros));
                }
            }

            await process.WaitForExitAsync(deadline.Token);
            return (process.ExitCode, await stderr);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            ct.ThrowIfCancellationRequested();
            throw new TimeoutException($"ffmpeg took longer than {timeout}.");
        }
    }

    /// <summary>Keeps the last few KiB of stderr: enough for the error, never a whole log.</summary>
    private static async Task<string> ReadTailAsync(StreamReader reader, CancellationToken ct)
    {
        const int keep = 4096;
        var tail = "";
        var buffer = new char[4096];
        int read;
        while ((read = await reader.ReadAsync(buffer, ct)) > 0)
        {
            tail = string.Concat(tail, buffer.AsSpan(0, read));
            if (tail.Length > keep)
            {
                tail = tail[^keep..];
            }
        }

        return tail.Trim();
    }
}
