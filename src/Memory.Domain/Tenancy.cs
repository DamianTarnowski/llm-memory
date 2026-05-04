namespace Memory.Domain;

public sealed class Organization
{
    public required OrganizationId Id { get; init; }
    public required string Slug { get; init; }
    public required string Name { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
}

public sealed class User
{
    public required UserId Id { get; init; }
    public required string Email { get; init; }
    public required string DisplayName { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
}

public sealed class Membership
{
    public required OrganizationId Organization { get; init; }
    public required UserId User { get; init; }
    public required MembershipRole Role { get; init; }
    public required DateTimeOffset GrantedAt { get; init; }
}

public enum MembershipRole
{
    Owner = 0,
    Admin = 1,
    Member = 2,
    Viewer = 3,
}

public sealed class Project
{
    public required ProjectId Id { get; init; }
    public required OrganizationId Organization { get; init; }
    public required string Slug { get; init; }
    public required string Name { get; init; }
    public required string EmbeddingModel { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
}

/// <summary>
/// Registry mapping an organization to its dedicated postgres schema for the
/// schema-per-org tenancy mode. The actual connection-routing is NOT yet wired —
/// this is a foundation table that the CLI's `tenants provision-schema` command
/// populates so future migrations can flip on per-org isolation incrementally.
/// </summary>
public sealed class TenantSchema
{
    public required OrganizationId Organization { get; init; }
    public required string SchemaName { get; init; }
    public required TenantSchemaStatus Status { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? ActivatedAt { get; init; }
}

public enum TenantSchemaStatus
{
    /// <summary>Schema created in postgres + registered, but RLS is still the active tenancy mode.</summary>
    Provisioned = 0,
    /// <summary>Connection-routing flipped — this schema is now serving the org's data.</summary>
    Active = 1,
    /// <summary>Org migrated off this schema (or was decommissioned).</summary>
    Deactivated = 2,
}
