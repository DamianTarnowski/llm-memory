using Memory.Domain;

namespace Memory.Pipeline.Search;

public interface IReranker
{
    Task<IReadOnlyList<SearchHit>> RerankAsync(
        string query,
        IReadOnlyList<SearchHit> candidates,
        CancellationToken ct = default);
}
