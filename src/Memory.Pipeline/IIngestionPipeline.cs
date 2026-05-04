namespace Memory.Pipeline;

public interface IIngestionPipeline
{
    Task<IngestionResult> IngestAsync(IngestionRequest request, CancellationToken ct = default);
}
