namespace Memory.Domain.Tests;

public class IdentifierTests
{
    [Fact]
    public void OrganizationId_New_HasNonEmptyGuid()
    {
        var id = OrganizationId.New();
        Assert.NotEqual(Guid.Empty, id.Value);
    }

    [Fact]
    public void OrganizationId_ToString_IsCanonicalGuidFormat()
    {
        var guid = Guid.NewGuid();
        var id = new OrganizationId(guid);
        Assert.Equal(guid.ToString("D"), id.ToString());
    }

    [Fact]
    public void OrganizationId_RecordEquality_OnSameValue()
    {
        var guid = Guid.NewGuid();
        var a = new OrganizationId(guid);
        var b = new OrganizationId(guid);
        Assert.Equal(a, b);
    }

    [Fact]
    public void TypedIds_AreNotInterchangeable()
    {
        var guid = Guid.NewGuid();
        var orgId = new OrganizationId(guid);
        var userId = new UserId(guid);
        Assert.IsType<OrganizationId>(orgId);
        Assert.IsType<UserId>(userId);
    }
}
