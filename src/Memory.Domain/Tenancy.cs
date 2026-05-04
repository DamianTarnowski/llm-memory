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
