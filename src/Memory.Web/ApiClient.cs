using System.Net.Http.Json;

namespace Memory.Web;

public sealed class ApiClient(HttpClient http)
{
    public async Task<List<EpisodeDto>> ListEpisodesAsync(int limit = 50, CancellationToken ct = default) =>
        await http.GetFromJsonAsync<List<EpisodeDto>>($"api/episodes?limit={limit}", ct) ?? new();

    public async Task<List<NoteDto>> ListNotesAsync(int limit = 50, CancellationToken ct = default) =>
        await http.GetFromJsonAsync<List<NoteDto>>($"api/notes?limit={limit}", ct) ?? new();

    public async Task<List<ReflectionDto>> ListReflectionsAsync(int limit = 20, CancellationToken ct = default) =>
        await http.GetFromJsonAsync<List<ReflectionDto>>($"api/reflections?limit={limit}", ct) ?? new();

    public async Task<List<EntityDto>> ListEntitiesAsync(string? name = null, int limit = 50, CancellationToken ct = default)
    {
        var url = string.IsNullOrEmpty(name)
            ? $"api/entities?limit={limit}"
            : $"api/entities?limit={limit}&name={Uri.EscapeDataString(name)}";
        return await http.GetFromJsonAsync<List<EntityDto>>(url, ct) ?? new();
    }

    public async Task<SearchResultDto?> SearchAsync(
        string query,
        int maxResults = 10,
        IReadOnlyList<string>? tags = null,
        DateTimeOffset? since = null,
        DateTimeOffset? until = null,
        CancellationToken ct = default)
    {
        var body = new
        {
            query,
            maxResults,
            tags,
            since,
            until,
        };
        var response = await http.PostAsJsonAsync("api/search", body, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<SearchResultDto>(cancellationToken: ct);
    }

    public async Task<List<EdgeDto>> ListEdgesAsync(int limit = 200, CancellationToken ct = default) =>
        await http.GetFromJsonAsync<List<EdgeDto>>($"api/edges?limit={limit}", ct) ?? new();

    // ----- /secrets (OpenBao admin) -----
    public async Task<SecretsStatusDto?> GetSecretsStatusAsync(CancellationToken ct = default) =>
        await http.GetFromJsonAsync<SecretsStatusDto>("api/secrets/status", ct);

    public async Task<List<string>> ListSecretPathsAsync(string? folder = null, CancellationToken ct = default)
    {
        var url = string.IsNullOrEmpty(folder) ? "api/secrets/paths" : $"api/secrets/paths?folder={Uri.EscapeDataString(folder)}";
        var response = await http.GetFromJsonAsync<SecretPathsDto>(url, ct);
        return response?.Paths?.ToList() ?? new();
    }

    public async Task<SecretDataDto?> GetSecretDataAsync(string path, CancellationToken ct = default) =>
        await http.GetFromJsonAsync<SecretDataDto>($"api/secrets/data?path={Uri.EscapeDataString(path)}", ct);

    public async Task<bool> WriteSecretDataAsync(string path, Dictionary<string, string> keys, CancellationToken ct = default)
    {
        var response = await http.PutAsJsonAsync("api/secrets/data", new { path, keys }, ct);
        return response.IsSuccessStatusCode;
    }

    public async Task<bool> DeleteSecretPathAsync(string path, CancellationToken ct = default)
    {
        var response = await http.DeleteAsync($"api/secrets/data?path={Uri.EscapeDataString(path)}", ct);
        return response.IsSuccessStatusCode;
    }
}

public sealed record EpisodeDto(Guid Id, string Source, string Content, DateTimeOffset IngestedAt, DateTimeOffset? OccurredAt);
public sealed record NoteDto(Guid Id, string Content, string ContextDescription, List<string> Keywords, List<string> Tags, DateTimeOffset CreatedAt);
public sealed record ReflectionDto(Guid Id, string Scope, string Summary, DateTimeOffset GeneratedAt, string GeneratorModel);
public sealed record EntityDto(Guid Id, string Name, string Kind, Dictionary<string, string> Attributes, DateTimeOffset FirstSeenAt, DateTimeOffset LastSeenAt);
public sealed record SearchHitDto(Guid NoteId, string Content, double Score, Guid[] RelatedEntityIds, SearchProvenanceDto? Provenance);
public sealed record SearchProvenanceDto(
    bool FromVector,
    bool FromBm25,
    bool FromGraph,
    double VectorScore,
    double Bm25Score,
    double GraphScore,
    double? RerankerScore);
public sealed record SearchResultDto(int TotalCandidates, List<SearchHitDto> Hits);
public sealed record EdgeDto(Guid Id, Guid From, Guid To, string Relation, DateTimeOffset RecordedAt, DateTimeOffset? InvalidatedAt);
public sealed record SecretsStatusDto(bool Configured, string Address, string Mount);
public sealed record SecretPathsDto(string[] Paths);
public sealed record SecretDataDto(string Path, Dictionary<string, string> Keys, int Version, DateTimeOffset? CreatedAt);
