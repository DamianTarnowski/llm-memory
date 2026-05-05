namespace Memory.Llm;

/// <summary>
/// Convert image bytes into a textual description so the rest of the pipeline
/// (extractor, embedder, retriever) can treat a multimodal episode as text.
/// First pass at multimodal — caption-based rather than embedding-the-image
/// directly. Pros: zero schema changes, image content participates in vector
/// + BM25 + graph retrieval through its caption immediately. Cons: lossy
/// compared to a real image embedding (CLIP / SigLIP). A future iteration can
/// add a parallel image-embedding column if the caption proves insufficient.
/// </summary>
public interface IImageDescriber
{
    Task<string> DescribeAsync(ReadOnlyMemory<byte> imageBytes, string mimeType, CancellationToken ct = default);
}
