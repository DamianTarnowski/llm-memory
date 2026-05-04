namespace Memory.Pipeline.Linking;

public sealed class LinkingOptions
{
    public const string SectionName = "Linking";

    public bool Enabled { get; set; } = true;
    public int NeighborCount { get; set; } = 5;
    public double MinSimilarity { get; set; } = 0.30;
    public double MinConfidence { get; set; } = 0.60;

    /// <summary>
    /// When the linker decides a new note "duplicates" an existing one with at least this
    /// confidence, automatically mark the NEW note as superseded so it doesn't pollute
    /// search results. The relation is still persisted as audit. Set to 1.01 to disable.
    /// </summary>
    public double DuplicateSupersedeConfidence { get; set; } = 0.90;
}
