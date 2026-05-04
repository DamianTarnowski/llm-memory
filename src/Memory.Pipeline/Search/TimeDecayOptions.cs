namespace Memory.Pipeline.Search;

public sealed class TimeDecayOptions
{
    public const string SectionName = "TimeDecay";

    /// <summary>
    /// When true, fused search hits are multiplied by an exponential recency factor before
    /// being passed to the reranker / returned. Older notes get gradually deprioritized so
    /// "what was I just working on?" beats "what once was true a year ago".
    /// </summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// Half-life in days — a note <see cref="HalfLifeDays"/> old keeps half its score, twice
    /// as old keeps a quarter, etc. Default: 30 days.
    /// </summary>
    public double HalfLifeDays { get; set; } = 30.0;

    /// <summary>
    /// Floor on the multiplier — even very old notes keep at least this fraction of their score
    /// so they can still be retrieved when query+content match strongly. 0.05 means old hits
    /// max out at a 20× discount.
    /// </summary>
    public double MinMultiplier { get; set; } = 0.05;
}
