using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;

namespace Memory.Llm.Providers;

internal sealed class VertexChatClient(
    VertexAccessTokenProvider tokens,
    string projectId,
    string location,
    string modelId) : IChatClient
{
    private readonly HttpClient _http = new();
    private bool _disposed;

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var url = $"https://aiplatform.googleapis.com/v1/projects/{projectId}/locations/{location}/publishers/google/models/{modelId}:generateContent";

        var body = BuildRequestBody(messages.ToList(), options);
        var token = await tokens.GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);

        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add("X-Goog-User-Project", projectId);
        request.Content = JsonContent.Create(body);

        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Vertex generateContent failed ({(int)response.StatusCode}): {raw}");
        }

        return ParseResponse(raw);
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
        foreach (var msg in response.Messages)
        {
            yield return new ChatResponseUpdate
            {
                Role = msg.Role,
                Contents = msg.Contents,
            };
        }
    }

    public object? GetService(Type serviceType, object? serviceKey) =>
        serviceType == typeof(IChatClient) ? this : null;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _http.Dispose();
    }

    private static JsonObject BuildRequestBody(IReadOnlyList<ChatMessage> messages, ChatOptions? options)
    {
        var contents = new JsonArray();
        JsonObject? systemInstruction = null;

        foreach (var msg in messages)
        {
            var text = msg.Text ?? string.Empty;
            if (msg.Role == ChatRole.System)
            {
                systemInstruction ??= new JsonObject { ["parts"] = new JsonArray() };
                ((JsonArray)systemInstruction["parts"]!).Add(new JsonObject { ["text"] = text });
                continue;
            }

            var role = msg.Role == ChatRole.Assistant ? "model" : "user";
            contents.Add(new JsonObject
            {
                ["role"] = role,
                ["parts"] = new JsonArray { new JsonObject { ["text"] = text } },
            });
        }

        var body = new JsonObject { ["contents"] = contents };
        if (systemInstruction is not null) body["systemInstruction"] = systemInstruction;

        if (options is not null)
        {
            var generationConfig = new JsonObject();
            if (options.Temperature is { } t) generationConfig["temperature"] = t;
            if (options.MaxOutputTokens is { } m) generationConfig["maxOutputTokens"] = m;
            if (options.TopP is { } p) generationConfig["topP"] = p;
            if (generationConfig.Count > 0) body["generationConfig"] = generationConfig;
        }

        return body;
    }

    private static ChatResponse ParseResponse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var candidates = doc.RootElement.GetProperty("candidates");
        if (candidates.GetArrayLength() == 0)
        {
            return new ChatResponse(new ChatMessage(ChatRole.Assistant, string.Empty));
        }

        var candidate = candidates[0];
        var contentEl = candidate.GetProperty("content");
        var parts = contentEl.GetProperty("parts");

        var text = string.Concat(parts.EnumerateArray()
            .Where(p => p.TryGetProperty("text", out _))
            .Select(p => p.GetProperty("text").GetString() ?? string.Empty));

        var msg = new ChatMessage(ChatRole.Assistant, text);
        var response = new ChatResponse(msg);

        if (candidate.TryGetProperty("finishReason", out var finishEl))
        {
            response.FinishReason = MapFinishReason(finishEl.GetString());
        }

        if (doc.RootElement.TryGetProperty("usageMetadata", out var usage))
        {
            response.Usage = new UsageDetails
            {
                InputTokenCount = usage.TryGetProperty("promptTokenCount", out var p) ? p.GetInt32() : null,
                OutputTokenCount = usage.TryGetProperty("candidatesTokenCount", out var c) ? c.GetInt32() : null,
                TotalTokenCount = usage.TryGetProperty("totalTokenCount", out var tk) ? tk.GetInt32() : null,
            };
        }

        return response;
    }

    private static ChatFinishReason? MapFinishReason(string? reason) => reason switch
    {
        "STOP" => ChatFinishReason.Stop,
        "MAX_TOKENS" => ChatFinishReason.Length,
        "SAFETY" => ChatFinishReason.ContentFilter,
        "RECITATION" => ChatFinishReason.ContentFilter,
        _ => null,
    };
}
