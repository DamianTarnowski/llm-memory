using Memory.Domain;
using Memory.Tenancy;

namespace Memory.Api;

internal sealed class TenantHeaderMiddleware(RequestDelegate next)
{
    public const string OrgHeader = "X-Memory-Org-Id";
    public const string UserHeader = "X-Memory-User-Id";
    public const string ProjectHeader = "X-Memory-Project-Id";

    public async Task InvokeAsync(HttpContext context, ITenantContext tenant)
    {
        var org = context.Request.Headers[OrgHeader].FirstOrDefault();
        var user = context.Request.Headers[UserHeader].FirstOrDefault();
        var project = context.Request.Headers[ProjectHeader].FirstOrDefault();

        if (Guid.TryParse(org, out var orgId)
            && Guid.TryParse(user, out var userId)
            && Guid.TryParse(project, out var projectId))
        {
            using var _ = tenant.BeginScope(new TenantScope(
                new OrganizationId(orgId),
                new UserId(userId),
                new ProjectId(projectId)));
            await next(context).ConfigureAwait(false);
        }
        else
        {
            await next(context).ConfigureAwait(false);
        }
    }
}
