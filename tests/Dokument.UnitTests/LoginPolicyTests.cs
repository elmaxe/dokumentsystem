using Dokument.Application.Identity;
using Dokument.Domain.Identity;

namespace Dokument.UnitTests;

public class LoginPolicyTests
{
    [Fact]
    public void Without_a_user_group_everyone_may_log_in()
    {
        var decision = new LoginPolicy(UserGroup: null, AdminGroup: "dokument-admins").Evaluate([]);

        Assert.Equal(new LoginDecision(true, UserRole.User), decision);
    }

    [Fact]
    public void The_user_group_is_an_allow_list()
    {
        var policy = new LoginPolicy(UserGroup: "dokument-users", AdminGroup: "dokument-admins");

        Assert.False(policy.Evaluate(["family"]).Allowed);
        Assert.Equal(
            new LoginDecision(true, UserRole.User),
            policy.Evaluate(["family", "dokument-users"])
        );
    }

    [Fact]
    public void Admins_are_allowed_even_outside_the_user_group()
    {
        var policy = new LoginPolicy(UserGroup: "dokument-users", AdminGroup: "dokument-admins");

        Assert.Equal(new LoginDecision(true, UserRole.Admin), policy.Evaluate(["dokument-admins"]));
    }

    [Fact]
    public void Group_names_are_case_sensitive()
    {
        var policy = new LoginPolicy(UserGroup: null, AdminGroup: "dokument-admins");

        Assert.Equal(UserRole.User, policy.Evaluate(["Dokument-Admins"]).Role);
    }
}
