namespace Memory.Domain.Tests;

public class SkillSyncPlannerTests
{
    private static RenderedSkill Rendered(string name, string content = "content") => new(name, content);

    private static SkillSyncManifest Manifest(params (string Name, string Content)[] owned) =>
        new(owned.Select(o => new SkillSyncEntry(
            o.Name,
            SkillSyncPlanner.RelativePath(o.Name),
            SkillSyncPlanner.ComputeHash(o.Content))).ToList());

    private static Dictionary<string, string> Files(params (string Name, string Content)[] files) =>
        files.ToDictionary(
            f => SkillSyncPlanner.RelativePath(f.Name),
            f => SkillSyncPlanner.ComputeHash(f.Content),
            StringComparer.Ordinal);

    [Fact]
    public void NewSkill_NoFile_Writes()
    {
        var plan = SkillSyncPlanner.Plan([Rendered("a")], Manifest(), Files());

        var action = Assert.Single(plan.Actions);
        Assert.Equal(SkillSyncActionKind.Write, action.Kind);
        Assert.Equal("a/SKILL.md", action.RelativePath);
        Assert.Equal("content", action.Content);
        Assert.Single(plan.NewManifest.Entries);
    }

    [Fact]
    public void NewSkill_IdenticalForeignFile_AdoptsOwnership()
    {
        var plan = SkillSyncPlanner.Plan([Rendered("a")], Manifest(), Files(("a", "content")));

        var action = Assert.Single(plan.Actions);
        Assert.Equal(SkillSyncActionKind.Adopt, action.Kind);
        Assert.Single(plan.NewManifest.Entries);
    }

    [Fact]
    public void NewSkill_DifferentForeignFile_NeverOverwritten()
    {
        var plan = SkillSyncPlanner.Plan([Rendered("a")], Manifest(), Files(("a", "hand-written")));

        var action = Assert.Single(plan.Actions);
        Assert.Equal(SkillSyncActionKind.ForeignConflict, action.Kind);
        Assert.Empty(plan.NewManifest.Entries); // ownership never claimed
    }

    [Fact]
    public void NewSkill_DifferentForeignFile_ForceServerOverwrites()
    {
        var plan = SkillSyncPlanner.Plan([Rendered("a")], Manifest(), Files(("a", "hand-written")), forceServer: true);

        var action = Assert.Single(plan.Actions);
        Assert.Equal(SkillSyncActionKind.Write, action.Kind);
    }

    [Fact]
    public void OwnedSkill_ContentChanged_Writes()
    {
        var plan = SkillSyncPlanner.Plan(
            [Rendered("a", "v2")],
            Manifest(("a", "v1")),
            Files(("a", "v1")));

        var action = Assert.Single(plan.Actions);
        Assert.Equal(SkillSyncActionKind.Write, action.Kind);
        Assert.Equal("v2", action.Content);
        Assert.Equal(SkillSyncPlanner.ComputeHash("v2"), plan.NewManifest.Entries.Single().ContentHash);
    }

    [Fact]
    public void OwnedSkill_Unchanged_NoActions()
    {
        var plan = SkillSyncPlanner.Plan(
            [Rendered("a", "v1")],
            Manifest(("a", "v1")),
            Files(("a", "v1")));

        Assert.Empty(plan.Actions);
        Assert.Single(plan.NewManifest.Entries);
    }

    [Fact]
    public void OwnedSkill_HandEdited_SkippedAndStillFlagged()
    {
        var plan = SkillSyncPlanner.Plan(
            [Rendered("a", "v2")],
            Manifest(("a", "v1")),
            Files(("a", "user-edit")));

        var action = Assert.Single(plan.Actions);
        Assert.Equal(SkillSyncActionKind.LocallyModified, action.Kind);
        // Old manifest entry survives so the next run flags the conflict again.
        Assert.Equal(SkillSyncPlanner.ComputeHash("v1"), plan.NewManifest.Entries.Single().ContentHash);
    }

    [Fact]
    public void OwnedSkill_HandEdited_ForceServerOverwrites()
    {
        var plan = SkillSyncPlanner.Plan(
            [Rendered("a", "v2")],
            Manifest(("a", "v1")),
            Files(("a", "user-edit")),
            forceServer: true);

        var action = Assert.Single(plan.Actions);
        Assert.Equal(SkillSyncActionKind.Write, action.Kind);
    }

    [Fact]
    public void OwnedSkill_FileVanished_Recreated()
    {
        var plan = SkillSyncPlanner.Plan(
            [Rendered("a", "v1")],
            Manifest(("a", "v1")),
            Files());

        var action = Assert.Single(plan.Actions);
        Assert.Equal(SkillSyncActionKind.Write, action.Kind);
    }

    [Fact]
    public void UnpublishedSkill_PristineFile_Deleted()
    {
        var plan = SkillSyncPlanner.Plan(
            [],
            Manifest(("gone", "v1")),
            Files(("gone", "v1")));

        var action = Assert.Single(plan.Actions);
        Assert.Equal(SkillSyncActionKind.Delete, action.Kind);
        Assert.Empty(plan.NewManifest.Entries);
    }

    [Fact]
    public void UnpublishedSkill_HandEditedFile_Kept()
    {
        var plan = SkillSyncPlanner.Plan(
            [],
            Manifest(("gone", "v1")),
            Files(("gone", "user-edit")));

        var action = Assert.Single(plan.Actions);
        Assert.Equal(SkillSyncActionKind.LocallyModified, action.Kind);
        Assert.Single(plan.NewManifest.Entries); // still owned, still flagged next run
    }

    [Fact]
    public void UnpublishedSkill_FileAlreadyGone_EntryDropped()
    {
        var plan = SkillSyncPlanner.Plan([], Manifest(("gone", "v1")), Files());

        Assert.Empty(plan.Actions);
        Assert.Empty(plan.NewManifest.Entries);
    }

    [Fact]
    public void ComputeHash_NormalizesLineEndings() =>
        Assert.Equal(
            SkillSyncPlanner.ComputeHash("a\r\nb"),
            SkillSyncPlanner.ComputeHash("a\nb"));

    [Fact]
    public void Manifest_RoundTripsThroughJson()
    {
        var manifest = Manifest(("a", "v1"), ("b", "v2"));
        var parsed = SkillSyncPlanner.ParseManifest(SkillSyncPlanner.SerializeManifest(manifest));

        Assert.Equal(2, parsed.Entries.Count);
        Assert.Equal(manifest.Entries[0], parsed.Entries[0]);
    }

    [Fact]
    public void Manifest_MalformedJson_ParsesAsEmpty() =>
        Assert.Empty(SkillSyncPlanner.ParseManifest("{broken").Entries);
}
