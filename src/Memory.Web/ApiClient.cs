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

    public async Task<SearchResultDto?> SearchAsync(string query, int maxResults = 10, CancellationToken ct = default)
    {
        var response = await http.PostAsJsonAsync("api/search", new { query, maxResults }, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<SearchResultDto>(cancellationToken: ct);
    }
}

public sealed record EpisodeDto(Guid Id, string Source, string Content, DateTimeOffset IngestedAt, DateTimeOffset? OccurredAt);
public sealed record NoteDto(Guid Id, string Content, string ContextDescription, List<string> Keywords, List<string> Tags, DateTimeOffset CreatedAt);
public sealed record ReflectionDto(Guid Id, string Scope, string Summary, DateTimeOffset GeneratedAt, string GeneratorModel);
public sealed record EntityDto(Guid Id, string Name, string Kind, Dictionary<string, string> Attributes, DateTimeOffset FirstSeenAt, DateTimeOffset LastSeenAt);
public sealed record SearchHitDto(Guid NoteId, string Content, double Score, Guid[] RelatedEntityIds);
public sealed record SearchResultDto(int TotalCandidates, List<SearchHitDto> Hits);
