namespace Memory.Pipeline.Ingestion;

internal interface IImportanceJudge
{
    Task<ImportanceJudgment> JudgeAsync(string source, string content, CancellationToken ct = default);
}

internal sealed record ImportanceJudgment(double Score, bool Save, string Reason);
