using Memory.Pipeline;
using Memory.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Memory.Pipeline.Tests;

[Collection(nameof(LivePipelineCollection))]
public class IngestionPipelineTests(LivePipelineFixture fixture)
{
    [LiveLlmFact(Skip = "Foundry gpt-5.5 occasionally returns JSON-schema-shaped output instead of values for nested structured-output records. Smoke test (live MCP via stdio) passes consistently with the same model — likely a request-shape difference between MEAI's GetResponseAsync<T> and the smoke test path. Investigate v1.x.")]
    public async Task Ingest_PersistsEpisodeNoteAndEmbedding()
    {
        using var _ = fixture.BeginTestScope();
        await using var scope = fixture.Services.CreateAsyncScope();

        var pipeline = scope.ServiceProvider.GetRequiredService<IIngestionPipeline>();
        var content = "Marie Skłodowska-Curie won the Nobel Prize in Physics in 1903 with her husband Pierre Curie.";

        var result = await pipeline.IngestAsync(new IngestionRequest("integration-test", content));

        Assert.NotEqual(default, result.EpisodeId);
        Assert.NotEmpty(result.Notes);

        await using var verifyScope = fixture.Services.CreateAsyncScope();
        var db = verifyScope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var episode = await db.Episodes.FirstOrDefaultAsync(e => e.Id == result.EpisodeId);
        var note = await db.Notes.FirstOrDefaultAsync(n => n.Id == result.Notes[0]);
        var embedding = await db.NoteEmbeddings.FirstOrDefaultAsync(e => e.NoteId == result.Notes[0]);

        Assert.NotNull(episode);
        Assert.Equal("integration-test", episode!.Source);
        Assert.NotNull(note);
        Assert.NotEmpty(note!.Content);
        Assert.NotNull(embedding);
        Assert.Equal(3072, embedding!.Dimensions);
    }

    [LiveLlmFact(Skip = "Foundry gpt-5.5 occasionally returns JSON-schema-shaped output instead of values for nested structured-output records. Smoke test (live MCP via stdio) passes consistently with the same model — likely a request-shape difference between MEAI's GetResponseAsync<T> and the smoke test path. Investigate v1.x.")]
    public async Task Search_FindsIngestedNote()
    {
        using var _ = fixture.BeginTestScope();
        await using var scope = fixture.Services.CreateAsyncScope();

        var ingest = scope.ServiceProvider.GetRequiredService<IIngestionPipeline>();
        await ingest.IngestAsync(new IngestionRequest(
            "integration-test",
            "Albert Einstein developed the theory of general relativity in 1915, building on his earlier special relativity work."));

        var search = scope.ServiceProvider.GetRequiredService<ISearchPipeline>();
        var result = await search.SearchAsync(new SearchRequest("who developed general relativity", 5));

        Assert.NotEmpty(result.Hits);
        Assert.Contains(result.Hits, h => h.Content.Contains("Einstein", StringComparison.OrdinalIgnoreCase) ||
                                          h.Content.Contains("relativity", StringComparison.OrdinalIgnoreCase));
    }
}
