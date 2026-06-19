namespace Memory.Pipeline.Search;

internal interface IQueryRouter
{
    Task<QueryRoute> RouteAsync(SearchRequest request, CancellationToken ct = default);
}
