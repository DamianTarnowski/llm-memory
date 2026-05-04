namespace Memory.Domain.Tests;

public class TenantScopeTests
{
    [Fact]
    public void TenantScope_RoundTrip_AllThreeIds()
    {
        var org = OrganizationId.New();
        var user = UserId.New();
        var project = ProjectId.New();

        var scope = new TenantScope(org, user, project);

        Assert.Equal(org, scope.Organization);
        Assert.Equal(user, scope.User);
        Assert.Equal(project, scope.Project);
    }

    [Fact]
    public void TenantScope_RecordEquality_OnSameIds()
    {
        var org = OrganizationId.New();
        var user = UserId.New();
        var project = ProjectId.New();

        var a = new TenantScope(org, user, project);
        var b = new TenantScope(org, user, project);

        Assert.Equal(a, b);
    }
}
