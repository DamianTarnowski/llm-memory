using Memory.Domain;

namespace Memory.Pipeline.Reflection;

public sealed record ReflectionRequest(
    string Scope = "recent",
    int MaxNotes = 30,
    DateTimeOffset? Since = null,
    DateTimeOffset? Until = null);

public sealed record ReflectionResult(
    ReflectionId Id,
    string Scope,
    string Summary,
    int NotesConsidered);

public interface IReflectionPipeline
{
    Task<ReflectionResult> ReflectAsync(ReflectionRequest request, CancellationToken ct = default);
}
