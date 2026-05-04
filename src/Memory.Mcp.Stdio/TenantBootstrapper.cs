using Memory.Domain;
using Memory.Tenancy;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Memory.Mcp.Stdio;

internal sealed class TenantBootstrapper(IOptions<StdioTenantOptions> options, ITenantContext tenant) : IHostedService
{
    private IDisposable? _scope;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var t = options.Value;
        if (t.OrganizationId == Guid.Empty || t.UserId == Guid.Empty || t.ProjectId == Guid.Empty)
        {
            throw new InvalidOperationException(
                "Tenant scope is not configured. Provide Tenant:OrganizationId, Tenant:UserId, Tenant:ProjectId via appsettings.json or environment variables (e.g. MEMORY_Tenant__ProjectId=...).");
        }

        _scope = tenant.BeginScope(new TenantScope(
            new OrganizationId(t.OrganizationId),
            new UserId(t.UserId),
            new ProjectId(t.ProjectId)));
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _scope?.Dispose();
        return Task.CompletedTask;
    }
}
