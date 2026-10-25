using System.ComponentModel;
using System.Diagnostics;

namespace Dokument.Worker.Search;

/// <summary>What a command-line tool printed, and how it ended.</summary>
public sealed record ToolOutput(int ExitCode, byte[] Stdout, string Stderr);

/// <summary>
/// Runs the command-line tools the worker relies on (exiftool, Tesseract), which ship in the image (docs/design.md
/// §10.3). Arguments go through <see cref="ProcessStartInfo.ArgumentList"/>, never a shell, and the process is killed
/// when the job is cancelled or the tool runs past its time.
/// </summary>
internal static class ExternalTool
{
    /// <summary>Stops reading output past this, so a misbehaving tool can't fill memory.</summary>
    private const int MaxOutputBytes = 64 * 1024 * 1024;

    public static async Task<ToolOutput> RunAsync(
        string fileName,
        IEnumerable<string> arguments,
        TimeSpan timeout,
        CancellationToken ct,
        byte[]? stdin = null
    )
    {
        var start = new ProcessStartInfo(fileName)
        {
            RedirectStandardInput = stdin is not null,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        // Tesseract uses every core by default; the worker's own concurrency setting decides that instead.
        start.Environment["OMP_THREAD_LIMIT"] = "1";

        using var process = new Process { StartInfo = start };
        try
        {
            process.Start();
        }
        catch (Win32Exception e)
        {
            throw new ToolMissingException(fileName, e);
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);
        try
        {
            var stdout = ReadLimitedAsync(process.StandardOutput.BaseStream, deadline.Token);
            var stderr = process.StandardError.ReadToEndAsync(deadline.Token);
            if (stdin is not null)
            {
                await process.StandardInput.BaseStream.WriteAsync(stdin, deadline.Token);
                process.StandardInput.Close();
            }

            await process.WaitForExitAsync(deadline.Token);
            return new ToolOutput(process.ExitCode, await stdout, await stderr);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            ct.ThrowIfCancellationRequested();
            throw new TimeoutException($"{fileName} took longer than {timeout}.");
        }
    }

    private static async Task<byte[]> ReadLimitedAsync(Stream stream, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read <= MaxOutputBytes)
            {
                buffer.Write(chunk, 0, read);
            }
        }

        return buffer.ToArray();
    }
}

/// <summary>A tool the image should have isn't installed, e.g. when running outside Docker without exiftool.</summary>
public sealed class ToolMissingException(string tool, Exception inner)
    : Exception($"\"{tool}\" isn't installed or can't be run.", inner);
