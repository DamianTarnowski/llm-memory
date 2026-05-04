namespace Memory.Domain;

public sealed record TenantScope(OrganizationId Organization, UserId User, ProjectId Project);
