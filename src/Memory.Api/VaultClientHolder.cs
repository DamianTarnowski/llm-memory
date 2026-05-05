using VaultSharp;

namespace Memory.Api;

/// <summary>
/// DI wrapper around a possibly-null <see cref="IVaultClient"/>. We register
/// the holder rather than the client itself because <c>AddSingleton&lt;IVaultClient?&gt;</c>
/// trips the class-constraint nullability warning. Endpoints check
/// <see cref="Client"/> for null and 503 if OpenBao isn't configured.
/// </summary>
public sealed class VaultClientHolder(IVaultClient? client)
{
    public IVaultClient? Client { get; } = client;
}
