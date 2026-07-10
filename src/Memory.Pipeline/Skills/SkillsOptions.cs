namespace Memory.Pipeline.Skills;

/// <summary>
/// Configuration for the skills subsystem (<c>Skills</c> section). All opt-in.
/// </summary>
public sealed class SkillsOptions
{
    public const string SectionName = "Skills";

    public bool Enabled { get; set; }

    /// <summary>
    /// How autonomous synthesis output is published. Applies to SYNTHESIZED skills
    /// only — explicit saves (<c>save_skill publish:true</c>) always publish directly;
    /// they are audited, versioned, and revertible, which is the safety mechanism.
    /// </summary>
    public SkillAutomationMode AutomationMode { get; set; } = SkillAutomationMode.Suggest;

    public SkillsSecurityOptions Security { get; set; } = new();

    public SkillSynthesisOptions Synthesis { get; set; } = new();
}

public sealed class SkillSynthesisOptions
{
    /// <summary>Sessions below this heuristic signal score are skipped without any LLM cost.</summary>
    public double MinSignalScore { get; set; } = 0.5;

    /// <summary>Cap per synthesis run — the primary cost control.</summary>
    public int MaxSessionsPerRun { get; set; } = 5;

    /// <summary>Cheap model for the Reflector / Curator / quality judge (ChatOptions.ModelId override).</summary>
    public string? ReflectorModelOverride { get; set; }

    /// <summary>Optional model override for the Drafter; null = default chat model.</summary>
    public string? DrafterModelOverride { get; set; }
}

public enum SkillAutomationMode
{
    /// <summary>Synthesized skills land as Candidate and wait for review.</summary>
    Suggest = 0,

    /// <summary>Synthesized skills publish immediately (versioned + audited + 1-click rollback).</summary>
    AutoExecute = 1,
}

public sealed class SkillsSecurityOptions
{
    /// <summary>
    /// When true (default), skills whose provenance includes untrusted web/tool
    /// content always land as Candidate, even in AutoExecute mode. Settable —
    /// secure default, owner's choice wins.
    /// </summary>
    public bool UntrustedInputForcesReview { get; set; } = true;
}
