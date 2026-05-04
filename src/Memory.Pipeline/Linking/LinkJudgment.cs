namespace Memory.Pipeline.Linking;

public sealed record LinkJudgment(
    bool IsRelated,
    string RelationType,
    double Confidence,
    string Description);
