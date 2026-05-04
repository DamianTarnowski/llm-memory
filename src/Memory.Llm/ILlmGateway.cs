using Microsoft.Extensions.AI;

namespace Memory.Llm;

public interface ILlmGateway
{
    IChatClient GetChat();
    IEmbeddingGenerator<string, Embedding<float>> GetEmbeddings();
}
