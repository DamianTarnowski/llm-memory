using System.Runtime.CompilerServices;
using Amazon;
using Amazon.BedrockRuntime;
using Amazon.BedrockRuntime.Model;
using Microsoft.Extensions.AI;

namespace Memory.Llm.Providers;

internal sealed class BedrockChatClient(string region, string modelId) : IChatClient
{
    private readonly AmazonBedrockRuntimeClient _client = new(RegionEndpoint.GetBySystemName(region));
    private bool _disposed;

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var (bedrockMessages, systemBlocks) = TranslateMessages(messages.ToList());

        var request = new ConverseRequest
        {
            ModelId = modelId,
            Messages = bedrockMessages,
            System = systemBlocks,
        };

        if (options is not null)
        {
            var inference = new InferenceConfiguration();
            if (options.Temperature is { } t) inference.Temperature = t;
            if (options.MaxOutputTokens is { } m) inference.MaxTokens = m;
            if (options.TopP is { } p) inference.TopP = p;
            request.InferenceConfig = inference;
        }

        var response = await _client.ConverseAsync(request, cancellationToken).ConfigureAwait(false);

        var responseText = response.Output?.Message?.Content?
            .Select(c => c.Text)
            .Where(t => !string.IsNullOrEmpty(t))
            .DefaultIfEmpty(string.Empty)
            .First() ?? string.Empty;

        var chatResponse = new ChatResponse(new ChatMessage(ChatRole.Assistant, responseText))
        {
            FinishReason = MapFinishReason(response.StopReason),
        };

        if (response.Usage is not null)
        {
            chatResponse.Usage = new UsageDetails
            {
                InputTokenCount = response.Usage.InputTokens,
                OutputTokenCount = response.Usage.OutputTokens,
                TotalTokenCount = response.Usage.TotalTokens,
            };
        }

        return chatResponse;
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
        _client.Dispose();
    }

    private static (List<Message> messages, List<SystemContentBlock> system) TranslateMessages(IReadOnlyList<ChatMessage> messages)
    {
        var bedrockMessages = new List<Message>();
        var systemBlocks = new List<SystemContentBlock>();

        foreach (var msg in messages)
        {
            var text = msg.Text ?? string.Empty;
            if (msg.Role == ChatRole.System)
            {
                systemBlocks.Add(new SystemContentBlock { Text = text });
                continue;
            }

            var role = msg.Role == ChatRole.Assistant ? ConversationRole.Assistant : ConversationRole.User;
            bedrockMessages.Add(new Message
            {
                Role = role,
                Content = [new ContentBlock { Text = text }],
            });
        }

        return (bedrockMessages, systemBlocks);
    }

    private static ChatFinishReason? MapFinishReason(StopReason? reason)
    {
        if (reason is null) return null;
        if (reason == StopReason.End_turn) return ChatFinishReason.Stop;
        if (reason == StopReason.Max_tokens) return ChatFinishReason.Length;
        if (reason == StopReason.Stop_sequence) return ChatFinishReason.Stop;
        if (reason == StopReason.Tool_use) return ChatFinishReason.ToolCalls;
        if (reason == StopReason.Content_filtered) return ChatFinishReason.ContentFilter;
        return null;
    }
}
