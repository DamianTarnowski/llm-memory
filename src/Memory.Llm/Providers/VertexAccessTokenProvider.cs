using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Memory.Llm.Providers;

internal sealed class VertexAccessTokenProvider(string adcCredentialsPath)
{
    private static readonly HttpClient _http = new();
    private static readonly TimeSpan _refreshSkew = TimeSpan.FromMinutes(5);
    private readonly SemaphoreSlim _gate = new(1, 1);

    private string? _accessToken;
    private DateTimeOffset _expiresAt = DateTimeOffset.MinValue;
    private AdcFile? _adc;

    public async Task<string> GetAccessTokenAsync(CancellationToken ct = default)
    {
        if (_accessToken is not null && DateTimeOffset.UtcNow + _refreshSkew < _expiresAt)
        {
            return _accessToken;
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_accessToken is not null && DateTimeOffset.UtcNow + _refreshSkew < _expiresAt)
            {
                return _accessToken;
            }

            _adc ??= LoadAdc();

            using var request = new HttpRequestMessage(HttpMethod.Post, "https://oauth2.googleapis.com/token");
            request.Content = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("client_id", _adc.ClientId),
                new KeyValuePair<string, string>("client_secret", _adc.ClientSecret),
                new KeyValuePair<string, string>("refresh_token", _adc.RefreshToken),
                new KeyValuePair<string, string>("grant_type", "refresh_token"),
            });

            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var token = await response.Content.ReadFromJsonAsync<TokenResponse>(cancellationToken: ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Vertex token endpoint returned empty body.");

            _accessToken = token.AccessToken;
            _expiresAt = DateTimeOffset.UtcNow.AddSeconds(token.ExpiresIn);
            return _accessToken!;
        }
        finally
        {
            _gate.Release();
        }
    }

    private AdcFile LoadAdc()
    {
        var path = Environment.ExpandEnvironmentVariables(adcCredentialsPath);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"Google ADC credentials file not found at '{path}'. Run 'gcloud auth application-default login' or set Llm:GoogleVertex:AdcCredentialsPath.");
        }

        using var stream = File.OpenRead(path);
        return JsonSerializer.Deserialize<AdcFile>(stream)
               ?? throw new InvalidOperationException($"Failed to parse ADC file at '{path}'.");
    }

    private sealed class AdcFile
    {
        [JsonPropertyName("client_id")] public string ClientId { get; set; } = string.Empty;
        [JsonPropertyName("client_secret")] public string ClientSecret { get; set; } = string.Empty;
        [JsonPropertyName("refresh_token")] public string RefreshToken { get; set; } = string.Empty;
    }

    private sealed class TokenResponse
    {
        [JsonPropertyName("access_token")] public string AccessToken { get; set; } = string.Empty;
        [JsonPropertyName("expires_in")] public int ExpiresIn { get; set; }
    }
}
