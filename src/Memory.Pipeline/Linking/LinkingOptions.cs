namespace Memory.Pipeline.Linking;

public sealed class LinkingOptions
{
    public const string SectionName = "Linking";

    public bool Enabled { get; set; } = true;
    public int NeighborCount { get; set; } = 5;
    public double MinSimilarity { get; set; } = 0.30;
    public double MinConfidence { get; set; } = 0.60;
}
