using Microsoft.Extensions.AI;

namespace Memory.Llm;

internal sealed class LlmImageDescriber(ILlmGateway llm) : IImageDescriber
{
    private const string SystemPrompt = """
        You convert an image into a precise, retrievable textual description for a personal
        knowledge-base. Describe what's actually visible: objects, people, text content (read
        and quote any visible text), layout, setting, notable details. 100-300 words. No
        speculation about what isn't pictured. No preamble — start with the description.
        """;

    public async Task<string> DescribeAsync(ReadOnlyMemory<byte> imageBytes, string mimeType, CancellationToken ct = default)
    {
        if (imageBytes.IsEmpty) return string.Empty;
        if (string.IsNullOrWhiteSpace(mimeType)) mimeType = "image/png";

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, SystemPrompt),
            new(ChatRole.User, new AIContent[]
            {
                new TextContent("Describe this image."),
                new DataContent(imageBytes.ToArray(), mimeType),
            }),
        };

        var response = await llm.GetChat().GetResponseAsync(messages, cancellationToken: ct).ConfigureAwait(false);
        return (response.Text ?? "").Trim();
    }
}
