namespace Memory.Pipeline.Ingestion;

public interface IExtractor
{
    Task<ExtractionResult> ExtractAsync(string content, CancellationToken ct = default);
}
