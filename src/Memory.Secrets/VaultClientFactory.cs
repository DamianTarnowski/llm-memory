using VaultSharp;
using VaultSharp.V1.AuthMethods;
using VaultSharp.V1.AuthMethods.AppRole;
using VaultSharp.V1.AuthMethods.Token;

namespace Memory.Secrets;

/// <summary>
/// Builds an <see cref="IVaultClient"/> for interactive CRUD against OpenBao /
/// HashiCorp Vault. Distinct from <see cref="OpenBaoConnector"/> — the connector
/// loads secrets once into <c>IConfiguration</c> at app startup; this factory
/// returns a live client that admin endpoints / UIs can use to read, write, and
/// delete secrets at request time.
/// </summary>
public static class VaultClientFactory
{
    /// <summary>
    /// Returns a client configured from <c>MEMORY_BAO_ADDR</c> +
    /// <c>MEMORY_BAO_TOKEN</c> (or AppRole id/secret). Returns <c>null</c>
    /// when no address or no auth is configured — callers register a nullable
    /// singleton in DI and serve a "not configured" response when that's the case.
    /// </summary>
    public static IVaultClient? FromEnvironment()
    {
        var address = Environment.GetEnvironmentVariable("MEMORY_BAO_ADDR");
        if (string.IsNullOrWhiteSpace(address)) return null;

        var token = Environment.GetEnvironmentVariable("MEMORY_BAO_TOKEN");
        var roleId = Environment.GetEnvironmentVariable("MEMORY_BAO_ROLE_ID");
        var secretId = Environment.GetEnvironmentVariable("MEMORY_BAO_SECRET_ID");

        IAuthMethodInfo auth;
        if (!string.IsNullOrWhiteSpace(token))
        {
            auth = new TokenAuthMethodInfo(token);
        }
        else if (!string.IsNullOrWhiteSpace(roleId) && !string.IsNullOrWhiteSpace(secretId))
        {
            auth = new AppRoleAuthMethodInfo(roleId, secretId);
        }
        else
        {
            return null;
        }

        var settings = new VaultClientSettings(address, auth);
        return new VaultClient(settings);
    }

    public static string Address => Environment.GetEnvironmentVariable("MEMORY_BAO_ADDR") ?? "";
    public static string KvMount => Environment.GetEnvironmentVariable("MEMORY_BAO_KV_MOUNT") ?? "secret";
}
