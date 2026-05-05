using Azure.Extensions.AspNetCore.Configuration.Secrets;
using Azure.Identity;
using Azure.Security.KeyVault.Secrets;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Memory;

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
            // Optional + unconfigured -> contribute zero keys.
            return new MemoryConfigurationProvider(new MemoryConfigurationSource());
        }

        var client = new SecretClient(new Uri(uri), new DefaultAzureCredential());
        var configOptions = new AzureKeyVaultConfigurationOptions
        {
            Manager = new KeyVaultSecretManager(),
            ReloadInterval = options.ReloadInterval,
        };
        var inner = new AzureKeyVaultConfigurationSource(client, configOptions);
        return inner.Build(builder);
    }
}
