using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Memory.Llm.Providers;

/// <summary>
/// Vertex <c>multimodalembedding@001</c> client — cross-modal embeddings (1408
/// dim) for both image bytes and text queries. Uses regional endpoints (the
/// model isn't published at <c>global</c>); region defaults to <c>us-central1</c>
/// because <c>multimodalembedding</c> is GA there. Auth via the same ADC token
/// provider as <see cref="VertexChatClient"/>.
/// </summary>
internal sealed class VertexImageEmbedder(
    VertexAccessTokenProvider tokens,
    string projectId,
    string region) : IImageEmbedder
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(45) };

    public int Dimensions => 1408;
    public string ModelId => "multimodalembedding@001";

    public Task<float[]> EmbedImageAsync(ReadOnlyMemory<byte> imageBytes, CancellationToken ct = default) =>
        EmbedAsync(new { image = new { bytesBase64Encoded = Convert.ToBase64String(imageBytes.Span) } }, expectImage: true, ct);

    public Task<float[]> EmbedTextAsync(string text, CancellationToken ct = default) =>
        EmbedAsync(new { text }, expectImage: false, ct);

    private async Task<float[]> EmbedAsync(object instance, bool expectImage, CancellationToken ct)
    {
        var url = $"https://{region}-aiplatform.googleapis.com/v1/projects/{projectId}/locations/{region}/publishers/google/models/{ModelId}:predict";
        var token = await tokens.GetAccessTokenAsync(ct).ConfigureAwait(false);

        using var req = new HttpRequestMessage(HttpMethod.Post, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Headers.Add("X-Goog-User-Project", projectId);
        req.Content = JsonContent.Create(new { instances = new[] { instance } });

        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        var raw = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Vertex multimodalembedding failed ({(int)resp.StatusCode}): {raw[..Math.Min(500, raw.Length)]}");
        }

        using var doc = JsonDocument.Parse(raw);
        var prediction = doc.RootElement.GetProperty("predictions")[0];
        var key = expectImage ? "imageEmbedding" : "textEmbedding";
        if (!prediction.TryGetProperty(key, out var emb))
        {
            // Fallback to "embedding" or whatever the response wrapper uses
            emb = prediction.EnumerateObject().First().Value;
        }
        var vec = new float[emb.GetArrayLength()];
        var i = 0;
        foreach (var v in emb.EnumerateArray()) vec[i++] = v.GetSingle();
        return vec;
    }
}
