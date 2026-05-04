using Memory.Domain;
using Memory.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace Memory.Storage.Tests;

[Collection(nameof(LivePgCollection))]
public class AgeGraphContextTests(LivePgFixture fixture)
{
    [Fact]
    public async Task UpsertEntity_CreatesNewNode_OnFirstCall()
    {
        using var _ = fixture.BeginTestScope();
        await using var scope = fixture.Services.CreateAsyncScope();
        var graph = scope.ServiceProvider.GetRequiredService<IGraphContext>();

        var id = await graph.UpsertEntityAsync(
            fixture.Project,
            name: "ada-lovelace",
            kind: "person",
            attributes: new Dictionary<string, string> { ["birth_year"] = "1815" },
            seenAt: DateTimeOffset.UtcNow);

        Assert.NotEqual(Guid.Empty, id.Value);
    }

    [Fact]
    public async Task UpsertEntity_PreservesId_OnSecondCall()
    {
        using var _ = fixture.BeginTestScope();
        await using var scope = fixture.Services.CreateAsyncScope();
        var graph = scope.ServiceProvider.GetRequiredService<IGraphContext>();

        var firstId = await graph.UpsertEntityAsync(
            fixture.Project, "alan-turing", "person",
            new Dictionary<string, string>(), DateTimeOffset.UtcNow);

        var secondId = await graph.UpsertEntityAsync(
            fixture.Project, "alan-turing", "person",
            new Dictionary<string, string> { ["birth_year"] = "1912" },
            DateTimeOffset.UtcNow);

        Assert.Equal(firstId, secondId);
    }

    [Fact]
    public async Task GetEntities_ReturnsByNameFilter()
    {
        using var _ = fixture.BeginTestScope();
        await using var scope = fixture.Services.CreateAsyncScope();
        var graph = scope.ServiceProvider.GetRequiredService<IGraphContext>();

        var now = DateTimeOffset.UtcNow;
        await graph.UpsertEntityAsync(fixture.Project, "linus-torvalds", "person", new Dictionary<string, string>(), now);
        await graph.UpsertEntityAsync(fixture.Project, "guido-van-rossum", "person", new Dictionary<string, string>(), now);

        var entities = await graph.GetEntitiesAsync(fixture.Project, nameFilter: "linus");

        Assert.Single(entities);
        Assert.Equal("linus-torvalds", entities[0].Name);
    }

    [Fact]
    public async Task AddEdge_AndGetEdges_RoundTrip()
    {
        using var _ = fixture.BeginTestScope();
        await using var scope = fixture.Services.CreateAsyncScope();
        var graph = scope.ServiceProvider.GetRequiredService<IGraphContext>();

        var now = DateTimeOffset.UtcNow;
        var alice = await graph.UpsertEntityAsync(fixture.Project, "alice-smith", "person",
            new Dictionary<string, string>(), now);
        var acme = await graph.UpsertEntityAsync(fixture.Project, "acme-corp", "organization",
            new Dictionary<string, string>(), now);

        await graph.AddEdgeAsync(new Edge
        {
            Id = EdgeId.New(),
            Project = fixture.Project,
            From = alice,
            To = acme,
            Relation = "WORKS_AT",
            RecordedAt = now,
            ValidFrom = now,
            Properties = new Dictionary<string, string> { ["title"] = "engineer" },
        });

        var outgoing = await graph.GetEdgesAsync(fixture.Project, from: alice);

        Assert.Single(outgoing);
        Assert.Equal("WORKS_AT", outgoing[0].Relation);
        Assert.Equal(alice, outgoing[0].From);
        Assert.Equal(acme, outgoing[0].To);
        Assert.Equal("engineer", outgoing[0].Properties["title"]);
    }
}
