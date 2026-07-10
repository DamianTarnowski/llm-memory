using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Memory.Domain;

/// <summary>
/// Which agent family a SKILL.md render targets. The two flavors differ only in
/// frontmatter: Claude Code understands the <c>when_to_use</c> extension and
/// <c>paths</c>, while the open Agent Skills standard (Codex, Cursor, Gemini CLI,
/// Copilot) matches on <c>description</c> alone — so trigger context is folded in.
/// </summary>
public enum SkillRenderFlavor
{
    ClaudeCode = 0,
    AgentsStandard = 1,
}

/// <summary>
/// Pure SKILL.md logic: slug validation, body sanitization (no executable payloads
/// in v1), and rendering a <see cref="Skill"/> to spec-compliant markdown.
/// Kept dependency-free so the CLI and unit tests can use it directly.
/// </summary>
public static partial class SkillMarkdown
{
    public const int MaxNameLength = 64;
    public const int MaxDescriptionLength = 1024;

    /// <summary>Frontmatter extras allowed to pass through from FrontmatterExtraJson. Everything else is dropped.</summary>
    private static readonly string[] _claudeExtraKeys = ["argument-hint", "paths", "metadata"];
    private static readonly string[] _agentsExtraKeys = ["argument-hint", "metadata"];

    [GeneratedRegex("^[a-z0-9]+(-[a-z0-9]+)*$")]
    private static partial Regex NameRegex();

    public static bool IsValidName(string? name) =>
        !string.IsNullOrWhiteSpace(name)
        && name.Length <= MaxNameLength
        && NameRegex().IsMatch(name);

    /// <summary>Best-effort conversion of a free-form title into a valid slug; returns null when nothing usable remains.</summary>
    public static string? Slugify(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        var sb = new StringBuilder(title.Length);
        var lastWasHyphen = true; // suppress leading hyphens
        foreach (var ch in title.Trim().ToLowerInvariant())
        {
            if (ch is >= 'a' and <= 'z' or >= '0' and <= '9')
            {
                sb.Append(ch);
                lastWasHyphen = false;
            }
            else if (!lastWasHyphen)
            {
                sb.Append('-');
                lastWasHyphen = true;
            }
        }

        var slug = sb.ToString().TrimEnd('-');
        if (slug.Length > MaxNameLength)
        {
            slug = slug[..MaxNameLength].TrimEnd('-');
        }

        return IsValidName(slug) ? slug : null;
    }

    /// <summary>
    /// Strips executable payloads from a skill body: Claude Code dynamic-context
    /// lines (<c>!`cmd`</c>), multi-line <c>```!</c> fences, and any leading
    /// frontmatter block that would corrupt the rendered file.
    /// </summary>
    public static string SanitizeBody(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return string.Empty;
        }

        var text = body.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();

        // Drop a leading frontmatter block if the body accidentally contains one.
        if (text.StartsWith("---\n", StringComparison.Ordinal))
        {
            var end = text.IndexOf("\n---", 3, StringComparison.Ordinal);
            if (end >= 0)
            {
                var after = text.IndexOf('\n', end + 1);
                text = after >= 0 ? text[(after + 1)..].TrimStart('\n') : string.Empty;
            }
        }

        // Remove ```! fenced blocks (dynamic multi-line shell execution) and
        // single-line !`cmd` dynamic-context invocations.
        var lines = text.Split('\n');
        var sb = new StringBuilder(text.Length);
        var insideBangFence = false;
        foreach (var line in lines)
        {
            var trimmed = line.TrimStart();
            if (insideBangFence)
            {
                if (trimmed.StartsWith("```", StringComparison.Ordinal))
                {
                    insideBangFence = false;
                }
                continue;
            }

            if (trimmed.StartsWith("```!", StringComparison.Ordinal))
            {
                insideBangFence = true;
                continue;
            }

            if (trimmed.StartsWith("!`", StringComparison.Ordinal))
            {
                continue;
            }

            sb.Append(line).Append('\n');
        }

        return sb.ToString().Trim();
    }

    public static string Render(Skill skill, SkillRenderFlavor flavor)
    {
        var sb = new StringBuilder();
        sb.Append("---\n");
        sb.Append("name: ").Append(skill.Name).Append('\n');

        var description = flavor == SkillRenderFlavor.AgentsStandard
            ? FoldDescription(skill.Description, skill.WhenToUse)
            : skill.Description;
        sb.Append("description: ").Append(YamlQuote(description)).Append('\n');

        if (flavor == SkillRenderFlavor.ClaudeCode && !string.IsNullOrWhiteSpace(skill.WhenToUse))
        {
            sb.Append("when_to_use: ").Append(YamlQuote(skill.WhenToUse)).Append('\n');
        }

        AppendExtras(sb, skill.FrontmatterExtraJson,
            flavor == SkillRenderFlavor.ClaudeCode ? _claudeExtraKeys : _agentsExtraKeys);

        sb.Append("---\n\n");
        sb.Append(SanitizeBody(skill.Body));
        sb.Append('\n');
        return sb.ToString();
    }

    /// <summary>Folds when_to_use trigger context into the description, respecting the spec's 1024-char cap.</summary>
    public static string FoldDescription(string description, string? whenToUse)
    {
        if (string.IsNullOrWhiteSpace(whenToUse))
        {
            return description;
        }

        var folded = $"{description.TrimEnd().TrimEnd('.')}. When to use: {whenToUse.Trim()}";
        if (folded.Length <= MaxDescriptionLength)
        {
            return folded;
        }

        return folded[..(MaxDescriptionLength - 1)].TrimEnd() + "…";
    }

    private static void AppendExtras(StringBuilder sb, string? extraJson, string[] allowedKeys)
    {
        if (string.IsNullOrWhiteSpace(extraJson))
        {
            return;
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(extraJson);
        }
        catch (JsonException)
        {
            return; // malformed extras never break a render
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            foreach (var key in allowedKeys)
            {
                if (!doc.RootElement.TryGetProperty(key, out var value))
                {
                    continue;
                }

                switch (key)
                {
                    case "argument-hint" when value.ValueKind == JsonValueKind.String:
                        sb.Append("argument-hint: ").Append(YamlQuote(value.GetString()!)).Append('\n');
                        break;

                    case "paths" when value.ValueKind == JsonValueKind.Array:
                        var globs = value.EnumerateArray()
                            .Where(e => e.ValueKind == JsonValueKind.String)
                            .Select(e => YamlQuote(e.GetString()!))
                            .ToArray();
                        if (globs.Length > 0)
                        {
                            sb.Append("paths: [").Append(string.Join(", ", globs)).Append("]\n");
                        }
                        break;

                    case "metadata" when value.ValueKind == JsonValueKind.Object:
                        var scalars = value.EnumerateObject()
                            .Where(p => p.Value.ValueKind is JsonValueKind.String or JsonValueKind.Number
                                or JsonValueKind.True or JsonValueKind.False)
                            .ToArray();
                        if (scalars.Length > 0)
                        {
                            sb.Append("metadata:\n");
                            foreach (var prop in scalars)
                            {
                                var raw = prop.Value.ValueKind == JsonValueKind.String
                                    ? prop.Value.GetString()!
                                    : prop.Value.GetRawText();
                                sb.Append("  ").Append(prop.Name).Append(": ").Append(YamlQuote(raw)).Append('\n');
                            }
                        }
                        break;
                }
            }
        }
    }

    private static string YamlQuote(string value)
    {
        var flat = value.Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal)
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal);
        return $"\"{flat}\"";
    }
}
