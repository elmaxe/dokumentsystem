using Dokument.Host;

namespace Dokument.UnitTests;

public class HostRolesTests
{
    [Theory]
    [InlineData(null, true, true)]
    [InlineData("", true, true)]
    [InlineData("server", true, false)]
    [InlineData("worker", false, true)]
    [InlineData("server,worker", true, true)]
    [InlineData(" Worker , SERVER ", true, true)]
    public void Parse_reads_roles(string? value, bool server, bool worker)
    {
        var roles = HostRoles.Parse(value);

        Assert.Equal(server, roles.Server);
        Assert.Equal(worker, roles.Worker);
    }

    [Fact]
    public void Parse_rejects_unknown_role()
    {
        Assert.Throws<ArgumentException>(() => HostRoles.Parse("scanner"));
    }
}
