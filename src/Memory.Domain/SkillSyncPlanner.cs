using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Memory.Domain;

/// <summary>
/// Pure planning logic for <c>memory skills sync</c>. The planner compares the
/// desired rendered skills against an ownership manifest and the current on-disk
/// file hashes, and emits actions. It never touches files it does not own:
/// foreign files are skipped, and owned files edited by hand are reported as
/// locally modified instead of being overwritten (unless forced).
/// </summary>
public static class SkillSyncPlanner
{
    public const string ManifestFileName = ".llm-memory-sync.json";

    public static SkillSyncPlan Plan(
        IReadOnlyList<RenderedSkill> desired,
        SkillSyncManifest manifest,
        IReadOnlyDictionary<string, string> existingFileHashes,
        bool forceServer = false)
    {
        var actions = new List<SkillSyncAction>();
        var newEntries = new List<SkillSyncEntry>();
        var desiredNames = new HashSet<string>(desired.Select(d => d.Name), StringComparer.Ordinal);
        var manifestByName = manifest.Entries.ToDictionary(e => e.Name, StringComparer.Ordinal);

        foreach (var skill in desired)
        {
            var relPath = RelativePath(skill.Name);
            var desiredHash = ComputeHash(skill.Content);
            existingFileHashes.TryGetValue(relPath, out var fileHash);
            manifestByName.TryGetValue(skill.Name, out var owned);

            if (owned is null)
            {
                if (fileHash is null)
                {
                    actions.Add(SkillSyncAction.Write(skill.Name, relPath, skill.Content));
                    newEntries.Add(new SkillSyncEntry(skill.Name, relPath, desiredHash));
                }
                else if (fileHash == desiredHash)
                {
                    // Identical content already on disk — adopt ownership silently.
                    actions.Add(SkillSyncAction.Adopt(skill.Name, relPath));
                    newEntries.Add(new SkillSyncEntry(skill.Name, relPath, desiredHash));
                }
                else if (forceServer)
                {
                    actions.Add(SkillSyncAction.Write(skill.Name, relPath, skill.Content));
                    newEntries.Add(new SkillSyncEntry(skill.Name, relPath, desiredHash));
                }
                else
                {
                    // A hand-written skill of the same name exists — never clobber it.
                    actions.Add(SkillSyncAction.ForeignConflict(skill.Name, relPath));
                }

                continue;
            }

            if (fileHash is null)
            {
                // Owned file vanished — recreate it.
                actions.Add(SkillSyncAction.Write(skill.Name, relPath, skill.Content));
                newEntries.Add(new SkillSyncEntry(skill.Name, relPath, desiredHash));
            }
            else if (fileHash == owned.ContentHash)
            {
                if (desiredHash != fileHash)
                {
                    actions.Add(SkillSyncAction.Write(skill.Name, relPath, skill.Content));
                }
                newEntries.Add(new SkillSyncEntry(skill.Name, relPath, desiredHash));
            }
            else if (forceServer)
            {
                actions.Add(SkillSyncAction.Write(skill.Name, relPath, skill.Content));
                newEntries.Add(new SkillSyncEntry(skill.Name, relPath, desiredHash));
            }
            else
            {
                // Owned file was edited by hand: keep the local edit, keep flagging it.
                actions.Add(SkillSyncAction.LocallyModified(skill.Name, relPath));
                newEntries.Add(owned);
            }
        }

        // Skills that left the published set: delete their files if still pristine.
        foreach (var owned in manifest.Entries.Where(e => !desiredNames.Contains(e.Name)))
        {
            existingFileHashes.TryGetValue(owned.RelativePath, out var fileHash);
            if (fileHash is null)
            {
                continue; // already gone; drop the entry
            }

            if (fileHash == owned.ContentHash || forceServer)
            {
                actions.Add(SkillSyncAction.Delete(owned.Name, owned.RelativePath));
            }
            else
            {
                actions.Add(SkillSyncAction.LocallyModified(owned.Name, owned.RelativePath));
                newEntries.Add(owned);
            }
        }

        return new SkillSyncPlan(actions, new SkillSyncManifest(newEntries));
    }

    public static string RelativePath(string skillName) => $"{skillName}/SKILL.md";

    public static string ComputeHash(string content)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(content.Replace("\r\n", "\n", StringComparison.Ordinal)));
        return Convert.ToHexStringLower(bytes);
    }

    public static SkillSyncManifest ParseManifest(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new SkillSyncManifest([]);
        }

        try
        {
            return JsonSerializer.Deserialize<SkillSyncManifest>(json, _jsonOptions) ?? new SkillSyncManifest([]);
        }
        catch (JsonException)
        {
            return new SkillSyncManifest([]);
        }
    }

    public static string SerializeManifest(SkillSyncManifest manifest) =>
        JsonSerializer.Serialize(manifest, _jsonOptions);

    private static readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
}

public sealed record RenderedSkill(string Name, string Content);

public sealed record SkillSyncEntry(string Name, string RelativePath, string ContentHash);

public sealed record SkillSyncManifest(List<SkillSyncEntry> Entries);

public enum SkillSyncActionKind
{
    /// <summary>Create or overwrite the file with server content.</summary>
    Write = 0,
    /// <summary>Identical file already present — record ownership, no write.</summary>
    Adopt = 1,
    /// <summary>Remove a file the sync owns whose skill left the published set.</summary>
    Delete = 2,
    /// <summary>Owned file was hand-edited — skipped; resolve with --force-server or import the edit.</summary>
    LocallyModified = 3,
    /// <summary>A foreign (never-owned) file occupies the slot — skipped, never overwritten.</summary>
    ForeignConflict = 4,
}

public sealed record SkillSyncAction(SkillSyncActionKind Kind, string Name, string RelativePath, string? Content)
{
    public static SkillSyncAction Write(string name, string relPath, string content) => new(SkillSyncActionKind.Write, name, relPath, content);
    public static SkillSyncAction Adopt(string name, string relPath) => new(SkillSyncActionKind.Adopt, name, relPath, null);
    public static SkillSyncAction Delete(string name, string relPath) => new(SkillSyncActionKind.Delete, name, relPath, null);
    public static SkillSyncAction LocallyModified(string name, string relPath) => new(SkillSyncActionKind.LocallyModified, name, relPath, null);
    public static SkillSyncAction ForeignConflict(string name, string relPath) => new(SkillSyncActionKind.ForeignConflict, name, relPath, null);
}

public sealed record SkillSyncPlan(List<SkillSyncAction> Actions, SkillSyncManifest NewManifest);
