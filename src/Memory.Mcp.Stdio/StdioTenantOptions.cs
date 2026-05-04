namespace Memory.Mcp.Stdio;

public sealed class StdioTenantOptions
{
    public const string SectionName = "Tenant";

    public Guid OrganizationId { get; set; }
    public Guid UserId { get; set; }
    public Guid ProjectId { get; set; }
}
