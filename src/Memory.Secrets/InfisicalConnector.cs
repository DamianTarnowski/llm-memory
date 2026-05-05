using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;

namespace Memory.Secrets;

public sealed class InfisicalOptions
{
    /// <summary>Base URL — defaults to Infisical Cloud (<c>https://app.infisical.com</c>) or read from MEMORY_INFISICAL_HOST.</summary>
    public string? Host { get; set; }

    /// <summary>Universal Auth client id (env: MEMORY_INFISICAL_CLIENT_ID).</summary>
    public string? ClientId { get; set; }

    /// <summary>Universal Auth client secret (env: MEMORY_INFISICAL_CLIENT_SECRET).</summary>
    public string? ClientSecret { get; set; }

    /// <summary>Project (workspace) id (env: MEMORY_INFISICAL_PROJECT_ID).</summary>
    public string? ProjectId { get; set; }

    /// <summary>Environment slug — typically "dev" / "prod" (env: MEMORY_INFISICAL_ENV). Default: "dev".</summary>
    public string Environment { get; set; } = "dev";

    /// <summary>Secret path inside the project (env: MEMORY_INFISICAL_PATH). Default: "/".</summary>
    public string SecretPath { get; set; } = "/";

    /// <summary>Skip silently when host/creds are missing instead of throwing. Default true.</summary>
    public bool Optional { get; set; } = true;

    /// <summary>HTTP timeout per request. Default 10s.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(10);
}

public sealed class InfisicalConnector(InfisicalOptions options) : ISecretConnector
{
    public string Name => "infisical";

    public IConfigurationProvider Build(IConfigurationBuilder builder) => new InfisicalConfigurationProvider(options);
}

internal sealed class InfisicalConfigurationProvider : ConfigurationProvider
{
    private readonly InfisicalOptions _options;
    public InfisicalConfigurationProvider(InfisicalOptions options) => _options = options;

    public override void Load()
    {
        var host = _options.Host ?? Environment.GetEnvironmentVariable("MEMORY_INFISICAL_HOST") ?? "https://app.infisical.com";
        var clientId = _options.ClientId ?? Environment.GetEnvironmentVariable("MEMORY_INFISICAL_CLIENT_ID");
        var clientSecret = _options.ClientSecret ?? Environment.GetEnvironmentVariable("MEMORY_INFISICAL_CLIENT_SECRET");
        var projectId = _options.ProjectId ?? Environment.GetEnvironmentVariable("MEMORY_INFISICAL_PROJECT_ID");
        var env = Environment.GetEnvironmentVariable("MEMORY_INFISICAL_ENV") ?? _options.Environment;
        var path = Environment.GetEnvironmentVariable("MEMORY_INFISICAL_PATH") ?? _options.SecretPath;

        var hasCreds = !string.IsNullOrEmpty(clientId)
                    && !string.IsNullOrEmpty(clientSecret)
                    && !string.IsNullOrEmpty(projectId);
        if (!hasCreds)
        {
            if (_options.Optional) return;
            throw new InvalidOperationException("Infisical connector is non-optional but client id / secret / project id are not configured.");
        }

        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(host), Timeout = _options.Timeout };

            // 1. Universal Auth login -> bearer.
            var login = http.PostAsJsonAsync("/api/v1/auth/universal-auth/login", new
            {
                clientId,
                clientSecret,
            }).GetAwaiter().GetResult();
            login.EnsureSuccessStatusCode();
            var loginBody = login.Content.ReadFromJsonAsync<UniversalAuthResponse>().GetAwaiter().GetResult()
                ?? throw new InvalidOperationException("Infisical: empty login response.");

            // 2. Fetch all secrets for env + path.
            using var req = new HttpRequestMessage(HttpMethod.Get,
                $"/api/v3/secrets/raw?workspaceId={Uri.EscapeDataString(projectId!)}&environment={Uri.EscapeDataString(env)}&secretPath={Uri.EscapeDataString(path)}&recursive=true");
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", loginBody.AccessToken);
            var resp = http.SendAsync(req).GetAwaiter().GetResult();
            resp.EnsureSuccessStatusCode();
            var body = resp.Content.ReadFromJsonAsync<SecretsResponse>().GetAwaiter().GetResult()
                ?? throw new InvalidOperationException("Infisical: empty secrets response.");

            var data = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in body.Secrets ?? Array.Empty<InfisicalSecret>())
            {
                if (string.IsNullOrEmpty(s.SecretKey)) continue;
                // Infisical disallows ':' in secret names; project convention is "Section__Sub" or
                // "Section--Sub" -> normalized to ".NET" colon-separated keys.
                var key = s.SecretKey.Replace("__", ":").Replace("--", ":");
                data[key] = s.SecretValue;
            }
            Data = data;
        }
        catch (Exception)
        {
            if (!_options.Optional) throw;
            // Optional + transient failure -> contribute nothing, fall through to next provider.
        }
    }

    private sealed record UniversalAuthResponse([property: JsonPropertyName("accessToken")] string AccessToken);
    private sealed record SecretsResponse([property: JsonPropertyName("secrets")] InfisicalSecret[]? Secrets);
    private sealed record InfisicalSecret(
        [property: JsonPropertyName("secretKey")] string SecretKey,
        [property: JsonPropertyName("secretValue")] string SecretValue);
}
