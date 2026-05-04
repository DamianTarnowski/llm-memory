namespace Memory.Pipeline;

public interface ISearchPipeline
{
    Task<SearchResult> SearchAsync(SearchRequest request, CancellationToken ct = default);
}
