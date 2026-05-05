using Azure.Extensions.AspNetCore.Configuration.Secrets;
using Azure.Identity;
using Azure.Security.KeyVault.Secrets;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Memory;
using Microsoft.Extensions.Primitives;

namespace Memory.Secrets;

public sealed class AzureKeyVaultOptions
{
    /// <summary>e.g. <c>https://llmmemory-kv.vault.azure.net/</c>. Read from MEMORY_KV_URI when null.</summary>
    public string? VaultUri { get; set; }

    /// <summary>
    /// Skip silently when no URI / no creds rather than throwing at startup.
    /// Default true — keeps local dev unblocked when no Azure access is configured.
    /// </summary>
    public bool Optional { get; set; } = true;

    /// <summary>How often to re-poll Key Vault for secret rotations. Default: 30 min.</summary>
    public TimeSpan ReloadInterval { get; set; } = TimeSpan.FromMinutes(30);
}

/// <summary>
/// Pulls secrets from Azure Key Vault using DefaultAzureCredential (az login locally,
/// Managed Identity in cloud). Secret names use <c>--</c> as the section separator
/// (Azure KV doesn't allow <c>:</c> in names) — the underlying SDK rewrites
/// <c>Llm--AzureOpenAi--ApiKey</c> back to <c>Llm:AzureOpenAi:ApiKey</c> automatically.
/// </summary>
public sealed class AzureKeyVaultConnector(AzureKeyVaultOptions options) : ISecretConnector
{
    public string Name => "azure-keyvault";

    public IConfigurationProvider Build(IConfigurationBuilder builder)
    {
        var uri = options.VaultUri ?? Environment.GetEnvironmentVariable("MEMORY_KV_URI");
        if (string.IsNullOrWhiteSpace(uri))
        {
            return new MemoryConfigurationProvider(new MemoryConfigurationSource());
        }

        // Wrap the inner provider in a fail-open shim so a transient KV outage at
        // startup (managed identity role still propagating, network blip, vault
        // briefly unreachable) doesn't crash the entire app — the rest of the
        // chain (OpenBao → JSON) carries the load.
        try
        {
            var client = new SecretClient(new Uri(uri), new DefaultAzureCredential());
            var configOptions = new AzureKeyVaultConfigurationOptions
            {
                Manager = new KeyVaultSecretManager(),
                ReloadInterval = options.ReloadInterval,
            };
            var inner = new AzureKeyVaultConfigurationSource(client, configOptions);
            var provider = inner.Build(builder);
            return new SafeKeyVaultProvider(provider, options.Optional);
        }
        catch when (options.Optional)
        {
            return new MemoryConfigurationProvider(new MemoryConfigurationSource());
        }
    }

    /// <summary>
    /// Decorates the underlying KV provider so its first synchronous Load() can fail
    /// without exiting the host. After Load() succeeds once, calls pass through.
    /// </summary>
    private sealed class SafeKeyVaultProvider(IConfigurationProvider inner, bool optional) : IConfigurationProvider, IDisposable
    {
        private bool _loaded;

        public IEnumerable<string> GetChildKeys(IEnumerable<string> earlierKeys, string? parentPath) =>
            _loaded ? inner.GetChildKeys(earlierKeys, parentPath) : earlierKeys;
        public IChangeToken GetReloadToken() => inner.GetReloadToken();
        public void Set(string key, string? value) => inner.Set(key, value);
        public bool TryGet(string key, out string? value)
        {
            if (!_loaded) { value = null; return false; }
            return inner.TryGet(key, out value);
        }
        public void Load()
        {
            try { inner.Load(); _loaded = true; }
            catch when (optional) { /* swallow — chain falls through */ }
        }
        public void Dispose() => (inner as IDisposable)?.Dispose();
    }
}
