using System.ComponentModel;
using System.Diagnostics;
using Npgsql;

namespace Dokument.Infrastructure.Backups;

/// <summary>The <c>Backups</c> configuration section (docs/design.md §19).</summary>
public sealed class BackupOptions
{
    public const string SectionName = "Backups";

    /// <summary>
    /// The <c>pg_dump</c> command: a name on PATH, or a full path. Its major version must be at least the server's,
    /// so the image ships the client of our Postgres image's version.
    /// </summary>
    public string PgDumpCommand { get; set; } = "pg_dump";

    /// <summary>The longest one backup may take before it is stopped and counts as failed.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromHours(2);
}

/// <summary>Why <c>pg_dump</c> didn't make a dump: missing, or what it printed when it failed.</summary>
public sealed class PgDumpException(string message, Exception? inner = null)
    : Exception(message, inner);

/// <summary>
/// Runs <c>pg_dump -Fc</c> (docs/design.md §19). The connection comes from the app's own connection string, passed
/// in <c>PG*</c> variables so the password never shows in a process list.
/// </summary>
public static class PgDump
{
    public static async Task RunAsync(
        string command,
        string connectionString,
        string outputPath,
        CancellationToken ct
    )
    {
        var connection = new NpgsqlConnectionStringBuilder(connectionString);
        var start = new ProcessStartInfo(command)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (
            var argument in new[] { "--format=custom", "--no-password", $"--file={outputPath}" }
        )
        {
            start.ArgumentList.Add(argument);
        }

        start.Environment["PGHOST"] = connection.Host;
        start.Environment["PGPORT"] = connection.Port.ToString(
            System.Globalization.CultureInfo.InvariantCulture
        );
        start.Environment["PGDATABASE"] = connection.Database;
        start.Environment["PGUSER"] = connection.Username;
        start.Environment["PGPASSWORD"] = connection.Password;
        start.Environment["PGAPPNAME"] = "dokument-backup";
        if (connection.SslMode is not SslMode.Prefer)
        {
            start.Environment["PGSSLMODE"] = connection.SslMode switch
            {
                SslMode.Disable => "disable",
                SslMode.Allow => "allow",
                SslMode.Require => "require",
                SslMode.VerifyCA => "verify-ca",
                SslMode.VerifyFull => "verify-full",
                _ => "prefer",
            };
        }

        using var process = new Process { StartInfo = start };
        try
        {
            process.Start();
        }
        catch (Win32Exception e)
        {
            throw new PgDumpException($"{command} wasn't found.", e);
        }

        try
        {
            var stdout = process.StandardOutput.ReadToEndAsync(ct);
            var stderr = process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);
            await stdout;
            var errors = (await stderr).Trim();
            if (process.ExitCode != 0)
            {
                throw new PgDumpException(
                    errors.Length > 0 ? errors : $"{command} exited with {process.ExitCode}."
                );
            }
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }
    }
}
