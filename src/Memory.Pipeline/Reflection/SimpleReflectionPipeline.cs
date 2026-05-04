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
    private const string SystemPrompt = """
        You synthesize a set of memory notes into a single reflection. Identify:
          - 2-5 recurring themes
          - tensions, contradictions, or open questions
          - actionable insights or next steps the agent should remember

        Be concise (under 400 words). Write in the second person ("you have been...") since this
        reflection will be presented back to the user as part of their long-term memory.
        Do not enumerate every note; abstract.
        """;

    public async Task<ReflectionResult> ReflectAsync(ReflectionRequest request, CancellationToken ct = default)
    {
        var scope = tenant.Require();
        var now = time.GetUtcNow();

        var sinceDefault = request.Since ?? now.AddDays(-7);
        var until = request.Until ?? now;

        var notes = await db.Notes
            .Where(n => n.SupersededAt == null
                        && n.CreatedAt >= sinceDefault
                        && n.CreatedAt <= until)
            .OrderByDescending(n => n.CreatedAt)
            .Take(request.MaxNotes)
            .Select(n => new { n.Content, n.ContextDescription, n.CreatedAt })
            .ToListAsync(ct).ConfigureAwait(false);

        if (notes.Count == 0)
        {
            return new ReflectionResult(ReflectionId.New(), request.Scope,
                "No notes found in the requested window — nothing to reflect on yet.", 0);
        }

        var prompt = new StringBuilder();
        prompt.Append("Reflect on these ").Append(notes.Count).Append(" recent notes (most recent first):\n\n");
        foreach (var note in notes)
        {
            prompt.Append("- [").Append(note.CreatedAt.ToString("yyyy-MM-dd")).Append("] ");
            if (!string.IsNullOrWhiteSpace(note.ContextDescription))
            {
                prompt.Append('(').Append(note.ContextDescription).Append(") ");
            }
            prompt.Append(note.Content).Append('\n');
        }

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, SystemPrompt),
            new(ChatRole.User, prompt.ToString()),
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

        return new ReflectionResult(reflection.Id, request.Scope, summary, notes.Count);
    }
}
