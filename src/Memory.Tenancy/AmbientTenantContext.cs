using Memory.Domain;

namespace Memory.Tenancy;

public sealed class AmbientTenantContext : ITenantContext
{
    private static readonly AsyncLocal<TenantScope?> _current = new();

    public TenantScope? Current => _current.Value;

    public TenantScope Require() =>
        _current.Value ?? throw new InvalidOperationException(
            "No tenant scope is active on this async flow. Call BeginScope() first.");

    public IDisposable BeginScope(TenantScope scope)
    {
        var previous = _current.Value;
        _current.Value = scope;
        return new ScopeReleaser(previous);
    }

    private sealed class ScopeReleaser(TenantScope? previous) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _current.Value = previous;
        }
    }
}
