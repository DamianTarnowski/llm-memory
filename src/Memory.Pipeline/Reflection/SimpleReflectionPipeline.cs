using System.Text;
using Memory.Domain;
using Memory.Llm;
using Memory.Storage;
using Memory.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace Memory.Pipeline.Reflection;

internal sealed class SimpleReflectionPipeline(
    ITenantContext tenant,
    MemoryDbContext db,
    ILlmGateway llm,
    IOptions<LlmOptions> llmOptions,
    TimeProvider time) : IReflectionPipeline
{
    private const string NotePrompt = """
        You synthesize a set of memory notes into a single reflection. Identify:
          - 2-5 recurring themes
          - tensions, contradictions, or open questions
          - actionable insights or next steps the agent should remember

        Be concise (under 400 words). Write in the second person ("you have been...") since this
        reflection will be presented back to the user as part of their long-term memory.
        Do not enumerate every note; abstract.
        """;

    private const string MetaPrompt = """
        You synthesize a set of EARLIER REFLECTIONS into a higher-order strategic reflection
        (meta-reflection). Each input is itself a multi-paragraph summary of the project's notes
        from a particular window. Your task:
          - identify the longer-arc trajectory across reflections (what shifted, what compounded?)
          - call out themes that recurred across multiple reflections — those are the durable ones
          - flag contradictions between reflections (the agent's view drifted; why?)
          - extract one or two strategic insights that wouldn't be visible from any single reflection

        Be concise (under 500 words). Write in the second person, address the user directly.
        Do not enumerate the inputs; abstract.
        """;

    public async Task<ReflectionResult> ReflectAsync(ReflectionRequest request, CancellationToken ct = default)
    {
        var scope = tenant.Require();
        var now = time.GetUtcNow();

        var isMeta = request.Scope.Equals("meta", StringComparison.OrdinalIgnoreCase)
                  || request.Scope.StartsWith("meta:", StringComparison.OrdinalIgnoreCase);

        var sinceDefault = request.Since ?? now.AddDays(isMeta ? -90 : -7);
        var until = request.Until ?? now;

        string promptBody;
        int sourceCount;

        if (isMeta)
        {
            var reflections = await db.Reflections
                .Where(r => r.GeneratedAt >= sinceDefault
                            && r.GeneratedAt <= until
                            && r.Scope != request.Scope) // don't fold prior meta-reflections of the same scope back in
                .OrderByDescending(r => r.GeneratedAt)
                .Take(request.MaxNotes)
                .Select(r => new { r.Scope, r.Summary, r.GeneratedAt })
                .ToListAsync(ct).ConfigureAwait(false);

            sourceCount = reflections.Count;
            if (sourceCount == 0)
            {
                return new ReflectionResult(ReflectionId.New(), request.Scope,
                    "No reflections found in the requested window — generate some lower-level reflections first.", 0);
            }

            var sb = new StringBuilder();
            sb.Append("Synthesize across these ").Append(sourceCount).Append(" reflections (most recent first):\n\n");
            foreach (var r in reflections)
            {
                sb.Append("--- [").Append(r.GeneratedAt.ToString("yyyy-MM-dd")).Append("] scope=").Append(r.Scope).Append(" ---\n");
                sb.Append(r.Summary).Append("\n\n");
            }
            promptBody = sb.ToString();
        }
        else
        {
            var notes = await db.Notes
                .Where(n => n.SupersededAt == null
                            && n.CreatedAt >= sinceDefault
                            && n.CreatedAt <= until)
                .OrderByDescending(n => n.CreatedAt)
                .Take(request.MaxNotes)
                .Select(n => new { n.Content, n.ContextDescription, n.CreatedAt })
                .ToListAsync(ct).ConfigureAwait(false);

            sourceCount = notes.Count;
            if (sourceCount == 0)
            {
                return new ReflectionResult(ReflectionId.New(), request.Scope,
                    "No notes found in the requested window — nothing to reflect on yet.", 0);
            }

            var sb = new StringBuilder();
            sb.Append("Reflect on these ").Append(sourceCount).Append(" recent notes (most recent first):\n\n");
            foreach (var note in notes)
            {
                sb.Append("- [").Append(note.CreatedAt.ToString("yyyy-MM-dd")).Append("] ");
                if (!string.IsNullOrWhiteSpace(note.ContextDescription))
                {
                    sb.Append('(').Append(note.ContextDescription).Append(") ");
                }
                sb.Append(note.Content).Append('\n');
            }
            promptBody = sb.ToString();
        }

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, isMeta ? MetaPrompt : NotePrompt),
            new(ChatRole.User, promptBody),
        };

        var response = await llm.GetChat().GetResponseAsync(messages, cancellationToken: ct).ConfigureAwait(false);
        var summary = response.Text ?? string.Empty;

        var reflection = new Memory.Domain.Reflection
        {
            Id = ReflectionId.New(),
            Project = scope.Project,
            Scope = request.Scope,
            Summary = summary,
            GeneratedAt = now,
            GeneratorModel = llmOptions.Value.ChatModel,
        };
        db.Reflections.Add(reflection);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        return new ReflectionResult(reflection.Id, request.Scope, summary, sourceCount);
    }
}
