namespace Dokument.Host;

/// <summary>Which role(s) this process runs, from the <c>ROLE</c> environment variable (docs/design.md §3.1).</summary>
public sealed record HostRoles(bool Server, bool Worker)
{
    /// <summary>Parses <c>server</c>, <c>worker</c> or <c>server,worker</c>. Unset means both, which suits development and tiny installs.</summary>
    public static HostRoles Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return new HostRoles(Server: true, Worker: true);
        }

        var server = false;
        var worker = false;
        foreach (
            var part in value.Split(
                ',',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
            )
        )
        {
            switch (part.ToLowerInvariant())
            {
                case "server":
                    server = true;
                    break;
                case "worker":
                    worker = true;
                    break;
                default:
                    throw new ArgumentException(
                        $"Unknown ROLE '{part}'. Use server, worker or server,worker.",
                        nameof(value)
                    );
            }
        }

        return new HostRoles(server, worker);
    }

    public override string ToString() =>
        (Server, Worker) switch
        {
            (true, true) => "server,worker",
            (true, false) => "server",
            _ => "worker",
        };
}
