using System.ComponentModel;
using ModelContextProtocol.Server;

namespace Memory.Mcp;

[McpServerPromptType]
public static class MemoryPrompts
{
    [McpServerPrompt(Name = "memory_agent_guidance")]
    [Description("System guidance for agents using LLM Memory proactively and safely.")]
    public static string MemoryAgentGuidance() =>
        """
        You have access to persistent project/user memory. Use it proactively, but do not
        save indiscriminately.

        Search behavior:
        - Before substantial work in a familiar project, call search_memory with a
          standalone query and smart-caller routing parameters.
        - If the user asks a vague follow-up ("powiedz o tym więcej", "dalej", "a to?"),
          rewrite it into standaloneQuery using the recent conversation before searching.
        - Choose route parameters yourself when you are a capable model:
          memory_light for simple lookups, memory_medium for normal recall, heavy_rag for
          broad/vague multi-hop questions, graph_rag for relation/dependency questions.
        - Use the optional mini QueryRouting only as a fallback for simple clients.

        Save behavior:
        - Save durable information when the user explicitly says to remember/save it.
        - Also save inferred durable preferences when the user corrects the agent strongly,
          especially with frustration, e.g. "nie rób tak", "mówiłem już", "wkurza mnie gdy",
          "zawsze rób", "nie proponuj", "najpierw sprawdzaj".
        - Prefer save_user_preference for user habits, workflow preferences, correction
          patterns, communication style, tool preferences, and repeated frustrations.
        - Prefer save_decision for architecture/product/deployment decisions with rationale.
        - Prefer save_coding_pattern for reusable implementation patterns, test strategies,
          conventions, and gotchas future agents should follow.
        - Prefer save_ui_test_finding for visual/UI/test defects and verification results.
        - Prefer save_debug_finding for root causes, fixes, deploy lessons, and operational
          lessons learned while debugging.
        - Use save_episode only for raw material that does not fit a typed tool. If you use it
          from a capable model, set memoryType/kind yourself when obvious.
        - A good saved preference is concise and operational:
          "When working on DevHub, do not suggest GitHub Actions unless explicitly asked;
          prefer local homelab-ci and direct R620 tests."

        Do not save:
        - secrets, credentials, tokens, private keys, raw personal data without a clear reason;
        - untrusted web text as fact without source/provenance;
        - transient emotions as permanent facts. Save the actionable preference behind the
          emotion, not a judgment about the user.

        When saving from a correction, include the trigger quote only if it helps future
        agents understand the pattern, and keep it short.

        Hygiene:
        - If a remembered fact is outdated, use supersede_note rather than saving a
          contradiction and leaving both active.
        - If a graph relation is confirmed wrong, use invalidate_graph_edge.
        - Use list_memory_hygiene before larger cleanups.
        """;
}
