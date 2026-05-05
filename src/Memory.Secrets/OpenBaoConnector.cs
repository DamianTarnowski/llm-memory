using Microsoft.Extensions.Configuration;
using VaultSharp;
using VaultSharp.V1.AuthMethods;
using VaultSharp.V1.AuthMethods.AppRole;
using VaultSharp.V1.AuthMethods.Token;

namespace Memory.Secrets;

public sealed class OpenBaoOptions
{
    /// <summary>e.g. <c>http://127.0.0.1:8200</c>. Read from MEMORY_BAO_ADDR when null.</summary>
    public string? Address { get; set; }

    /// <summary>Direct token auth — read from MEMORY_BAO_TOKEN when null. If both Token and AppRole are set, Token wins.</summary>
    public string? Token { get; set; }

    /// <summary>AppRole role id — read from MEMORY_BAO_ROLE_ID. Used when no token is provided.</summary>
    public string? AppRoleRoleId { get; set; }

    /// <summary>AppRole secret id — read from MEMORY_BAO_SECRET_ID.</summary>
    public string? AppRoleSecretId { get; set; }

    /// <summary>KV v2 mount path. Read from MEMORY_BAO_KV_MOUNT, default <c>secret</c>.</summary>
    public string KvMountPath { get; set; } = "secret";

    /// <summary>
    /// Comma-separated list of KV v2 paths (under the mount) to load. Read from
    /// MEMORY_BAO_KV_PATHS. Default <c>llm-memory</c>. Each path's data dictionary
    /// becomes a flat set of keys merged into configuration.
    /// </summary>
    public string KvPaths { get; set; } = "llm-memory";

    /// <summary>Skip silently when address/auth are missing. Default true.</summary>
    public bool Optional { get; set; } = true;
}

/// <summary>
/// Pulls secrets from OpenBao (or HashiCorp Vault — same wire protocol via VaultSharp).
/// Auth precedence: explicit Token &gt; AppRole &gt; environment-variable Token.
///
/// Each KV v2 path you list contributes its <c>data</c> dictionary as flat config keys.
/// Convention: store keys with <c>__</c> or <c>--</c> as section separators (e.g.
/// <c>Llm__AzureOpenAi__ApiKey</c>) since OpenBao secret-key names disallow <c>:</c>;
/// they're rewritten back to .NET <c>:</c> form on load.
/// </summary>
public sealed class OpenBaoConnector(OpenBaoOptions options) : ISecretConnector
{
    public string Name => "openbao";

    public IConfigurationProvider Build(IConfigurationBuilder builder) => new OpenBaoConfigurationProvider(options);
}

internal sealed class OpenBaoConfigurationProvider(OpenBaoOptions options) : ConfigurationProvider
{
    public override void Load()
    {
        var address = options.Address ?? Environment.GetEnvironmentVariable("MEMORY_BAO_ADDR");
        var token = options.Token ?? Environment.GetEnvironmentVariable("MEMORY_BAO_TOKEN");
        var roleId = options.AppRoleRoleId ?? Environment.GetEnvironmentVariable("MEMORY_BAO_ROLE_ID");
        var secretId = options.AppRoleSecretId ?? Environment.GetEnvironmentVariable("MEMORY_BAO_SECRET_ID");
        var mount = Environment.GetEnvironmentVariable("MEMORY_BAO_KV_MOUNT") ?? options.KvMountPath;
        var pathsRaw = Environment.GetEnvironmentVariable("MEMORY_BAO_KV_PATHS") ?? options.KvPaths;

        if (string.IsNullOrWhiteSpace(address))
        {
            if (options.Optional) return;
            throw new InvalidOperationException("OpenBao connector is non-optional but address is not configured.");
        }

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
            if (options.Optional) return;
            throw new InvalidOperationException("OpenBao connector is non-optional but no auth (Token or AppRole) is configured.");
        }

        try
        {
            var settings = new VaultClientSettings(address, auth);
            var client = new VaultClient(settings);

            var paths = pathsRaw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var data = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

            foreach (var path in paths)
            {
                var read = client.V1.Secrets.KeyValue.V2.ReadSecretAsync(path: path, mountPoint: mount)
                    .GetAwaiter().GetResult();
                if (read.Data?.Data is null) continue;

                foreach (var kv in read.Data.Data)
                {
                    var key = kv.Key.Replace("__", ":").Replace("--", ":");
                    data[key] = kv.Value?.ToString();
                }
            }

            Data = data;
        }
        catch (Exception)
        {
            if (!options.Optional) throw;
            // Optional + transient failure -> contribute nothing, fall through to next provider.
        }
    }
}
