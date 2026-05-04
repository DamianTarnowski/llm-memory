using Memory.Domain;

namespace Memory.Pipeline.Search;

/// <summary>
/// HippoRAG-2-inspired retrieval: extract entity seeds from the query,
/// run personalized PageRank over the project's knowledge graph, then
/// score notes by aggregated PPR mass on their mentioned entities.
/// </summary>
internal interface IGraphRetriever
{
    Task<IReadOnlyList<GraphRetrievalHit>> RetrieveAsync(string query, int maxResults, CancellationToken ct = default);
}

internal sealed record GraphRetrievalHit(NoteId NoteId, string Content, double Score, EntityId[] RelatedEntities);
