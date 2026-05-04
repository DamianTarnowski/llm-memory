using Memory.Domain;

namespace Memory.Storage;

public interface IGraphContext
{
    Task<IReadOnlyList<Entity>> GetEntitiesAsync(
        ProjectId project,
        string? nameFilter = null,
        int limit = 50,
        CancellationToken ct = default);

    Task<IReadOnlyList<Edge>> GetEdgesAsync(
        ProjectId project,
        EntityId? from = null,
        EntityId? to = null,
        string? relation = null,
        DateTimeOffset? validAt = null,
        CancellationToken ct = default);

    Task<EntityId> UpsertEntityAsync(
        ProjectId project,
        string name,
        string kind,
        IReadOnlyDictionary<string, string> attributes,
        DateTimeOffset seenAt,
        CancellationToken ct = default);
    Task AddEdgeAsync(Edge edge, CancellationToken ct = default);
    Task InvalidateEdgeAsync(EdgeId id, DateTimeOffset at, CancellationToken ct = default);
}
