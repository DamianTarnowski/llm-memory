using Memory.Domain;
using Memory.Tenancy;

namespace Memory.Mcp.Stdio;

internal sealed class FixedTenantContext(TenantScope scope) : ITenantContext
{
    public TenantScope? Current => scope;
    public TenantScope Require() => scope;
    public IDisposable BeginScope(TenantScope newScope) =>
        throw new NotSupportedException(
            "FixedTenantContext is single-tenant per process — call BeginScope only on AmbientTenantContext (HTTP path).");
}
