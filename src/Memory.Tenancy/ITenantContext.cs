using Memory.Domain;

namespace Memory.Tenancy;

public interface ITenantContext
{
    TenantScope? Current { get; }
    TenantScope Require();
    IDisposable BeginScope(TenantScope scope);
}
