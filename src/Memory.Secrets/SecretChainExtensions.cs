using Microsoft.Extensions.Configuration;

namespace Memory.Secrets;

/// <summary>
/// Public surface of Memory.Secrets — three opt-in extension methods on
/// <see cref="IConfigurationBuilder"/> matching the three connector kinds.
/// Compose them in your Program.cs in the order fallback → primary; .NET
/// configuration semantics make later providers win the merge.
///
/// Example:
/// <code>
/// builder.Configuration
///     .AddSecretsJsonFile("appsettings.Local.json")          // baseline
///     .AddSecretsInfisical(o => { /* opts or env vars */ })  // secondary
///     .AddSecretsAzureKeyVault(o => o.VaultUri = "...");     // primary, wins
/// </code>
/// </summary>
public static class SecretChainExtensions
{
    public static IConfigurationBuilder AddSecretsAzureKeyVault(
        this IConfigurationBuilder builder,
        Action<AzureKeyVaultOptions>? configure = null)
    {
        var opts = new AzureKeyVaultOptions();
        configure?.Invoke(opts);
        builder.Add(new AzureKeyVaultConnector(opts));
        return builder;
    }

    public static IConfigurationBuilder AddSecretsInfisical(
        this IConfigurationBuilder builder,
        Action<InfisicalOptions>? configure = null)
    {
        var opts = new InfisicalOptions();
        configure?.Invoke(opts);
        builder.Add(new InfisicalConnector(opts));
        return builder;
    }

    public static IConfigurationBuilder AddSecretsJsonFile(
        this IConfigurationBuilder builder,
        string path,
        bool optional = true,
        bool reloadOnChange = true)
    {
        builder.Add(new JsonFileConnector(new JsonFileOptions
        {
            Path = path,
            Optional = optional,
            ReloadOnChange = reloadOnChange,
        }));
        return builder;
    }
}
