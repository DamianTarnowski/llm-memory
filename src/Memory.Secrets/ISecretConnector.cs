using Microsoft.Extensions.Configuration;

namespace Memory.Secrets;

/// <summary>
/// Marker for a Memory.Secrets connector — every implementation is a regular
/// <see cref="IConfigurationSource"/> wired through extension methods on
/// <see cref="IConfigurationBuilder"/>. The interface exists so consumers can
/// reflect on a chain (e.g. for diagnostic logging) and so the public surface
/// of Memory.Secrets is a single nameable contract.
///
/// .NET configuration semantics: providers added LATER win. Order your calls
/// from least-trusted (file fallback) to most-trusted (Key Vault primary)
/// so primary values override fallbacks. Each connector is <c>optional</c> —
/// if its source is unreachable / unconfigured, it contributes no keys and
/// the chain falls through to the previous provider.
/// </summary>
public interface ISecretConnector : IConfigurationSource
{
    /// <summary>Short name shown in startup logs ("azure-keyvault", "infisical", "json").</summary>
    string Name { get; }
}
