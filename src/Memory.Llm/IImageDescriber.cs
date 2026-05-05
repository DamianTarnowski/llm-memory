namespace Memory.Llm;

/// <summary>
/// Convert image bytes into a textual description so the rest of the pipeline
/// (extractor, embedder, retriever) can treat a multimodal episode as text.
/// Caption-based — pros: zero schema changes, image content participates in
/// vector + BM25 + graph retrieval through its caption immediately. Cons: lossy
/// compared to a direct image embedding. <see cref="IImageEmbedder"/> covers
/// the embedding path for true cross-modal retrieval.
/// </summary>
public interface IImageDescriber
{
    Task<string> DescribeAsync(ReadOnlyMemory<byte> imageBytes, string mimeType, CancellationToken ct = default);
}

/// <summary>
/// Cross-modal embedding — same vector space for both image bytes and text
/// queries. Lets a text query retrieve images by visual content, not just by
/// caption text. Currently implemented via Vertex multimodalembedding@001 (1408
/// dim). Skipped silently when the active LLM provider isn't Vertex.
/// </summary>
public interface IImageEmbedder
{
    int Dimensions { get; }
    string ModelId { get; }
    Task<float[]> EmbedImageAsync(ReadOnlyMemory<byte> imageBytes, CancellationToken ct = default);
    Task<float[]> EmbedTextAsync(string text, CancellationToken ct = default);
}

/// <summary>
/// DI wrapper around a possibly-null <see cref="IImageEmbedder"/> — nullable
/// service refs trip AddSingleton's class constraint, so we register the holder
/// instead. Consumers check <see cref="Embedder"/> for null and skip the
/// cross-modal path when not configured.
/// </summary>
public sealed class ImageEmbedderHolder(IImageEmbedder? embedder)
{
    public IImageEmbedder? Embedder { get; } = embedder;
}
