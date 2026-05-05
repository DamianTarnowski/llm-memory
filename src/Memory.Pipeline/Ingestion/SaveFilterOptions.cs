namespace Memory.Pipeline.Ingestion;

public sealed class SaveFilterOptions
{
    public const string SectionName = "SaveFilter";

    /// <summary>
    /// When true, every <c>save_episode</c> first goes through the LLM importance
    /// judge. Episodes scored below <see cref="MinScore"/> are dropped before
    /// extraction / storage / embedding — saving cost and reducing noise in the
    /// long-term memory.
    /// </summary>
    public bool Enabled { get; set; } = false;

    /// <summary>Lowest importance score (0-1) that still ingests. Default 0.30.</summary>
    public double MinScore { get; set; } = 0.30;

    /// <summary>Override model for the judgment call. Default: gateway's chat model.</summary>
    public string? ModelOverride { get; set; }
}
