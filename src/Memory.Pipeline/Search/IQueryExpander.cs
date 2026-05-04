namespace Memory.Pipeline.Search;

/// <summary>
/// Generates additional query variants for short / vague inputs. Returned list always
/// includes the original query as the first element. When expansion is disabled, off, or
/// the query is already long enough, returns <c>[original]</c> only.
/// </summary>
internal interface IQueryExpander
{
    Task<IReadOnlyList<string>> ExpandAsync(string original, CancellationToken ct = default);
}
