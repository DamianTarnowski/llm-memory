using System.Diagnostics;
using Memory.Domain;
using Npgsql;

namespace Memory.Cli;

/// <summary>
/// <c>memory skills</c> — renders published skills from the memory store into
/// agent-readable skill directories (Claude Code + the .agents cross-agent standard),
/// guarded by an ownership manifest so hand-written skills are never touched.
/// </summary>
internal static class SkillsCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0 || args[0] is "help" or "--help" or "-h")
        {
            PrintHelp();
            return 0;
        }

        try
        {
            return args[0] switch
            {
                "sync" => await SyncAsync(args[1..]).ConfigureAwait(false),
                "init-dirs" => InitDirs(args[1..]),
                "list" => await ListAsync(args[1..]).ConfigureAwait(false),
                "harvest" => await HarvestAsync(args[1..]).ConfigureAwait(false),
                _ => Fail($"Unknown subcommand '{args[0]}'. Run 'memory skills help'."),
            };
        }
        catch (ArgumentException ex)
        {
            return Fail(ex.Message);
        }
    }

    // ---------------------------------------------------------------- sync

    private static async Task<int> SyncAsync(string[] args)
    {
        var opts = ParseSyncArgs(args);
        var skills = await LoadPublishedSkillsAsync(opts).ConfigureAwait(false);

        if (!opts.Quiet)
        {
            Console.WriteLine($"{skills.Count} published skill(s) in project {opts.ProjectId:D}.");
        }

        var exitCode = 0;
        foreach (var target in BuildTargets(opts))
        {
            if (target.RepoRoot is not null && !EnsureRepoTargetSafe(target, opts))
            {
                exitCode = 2;
                continue;
            }

            var rendered = skills
                .Select(s => new RenderedSkill(s.Name, SkillMarkdown.Render(s, target.Flavor)))
                .ToList();

            var manifestPath = Path.Combine(target.Directory, SkillSyncPlanner.ManifestFileName);
            var manifest = SkillSyncPlanner.ParseManifest(
                File.Exists(manifestPath) ? await File.ReadAllTextAsync(manifestPath).ConfigureAwait(false) : null);

            var fileHashes = CollectFileHashes(target.Directory, rendered, manifest);
            var plan = SkillSyncPlanner.Plan(rendered, manifest, fileHashes, opts.ForceServer);

            var wrote = 0; var deleted = 0; var skipped = 0;
            foreach (var action in plan.Actions)
            {
                var fullPath = Path.Combine(target.Directory, action.RelativePath.Replace('/', Path.DirectorySeparatorChar));
                switch (action.Kind)
                {
                    case SkillSyncActionKind.Write:
                        if (!opts.DryRun)
                        {
                            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
                            await File.WriteAllTextAsync(fullPath, action.Content!).ConfigureAwait(false);
                        }
                        wrote++;
                        break;

                    case SkillSyncActionKind.Delete:
                        if (!opts.DryRun && File.Exists(fullPath))
                        {
                            File.Delete(fullPath);
                            var dir = Path.GetDirectoryName(fullPath)!;
                            if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
                            {
                                Directory.Delete(dir);
                            }
                        }
                        deleted++;
                        break;

                    case SkillSyncActionKind.LocallyModified:
                        Console.WriteLine($"  ! {action.Name}: locally modified — skipped (use --force-server to overwrite)");
                        skipped++;
                        break;

                    case SkillSyncActionKind.ForeignConflict:
                        Console.WriteLine($"  ! {action.Name}: a hand-written skill occupies {action.RelativePath} — skipped, never overwritten");
                        skipped++;
                        break;
                }
            }

            if (!opts.DryRun)
            {
                Directory.CreateDirectory(target.Directory);
                await File.WriteAllTextAsync(manifestPath, SkillSyncPlanner.SerializeManifest(plan.NewManifest))
                    .ConfigureAwait(false);
            }

            if (!opts.Quiet)
            {
                Console.WriteLine($"{target.Directory} [{target.Flavor}]: {wrote} written, {deleted} deleted, {skipped} skipped{(opts.DryRun ? " (dry-run)" : "")}");
            }
        }

        return exitCode;
    }

    private static List<SyncTarget> BuildTargets(SyncOptions opts)
    {
        var targets = new List<SyncTarget>();
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        if (opts.RepoDir is null)
        {
            targets.Add(new SyncTarget(
                opts.ClaudeDir ?? Path.Combine(home, ".claude", "skills"),
                SkillRenderFlavor.ClaudeCode, RepoRoot: null));
            targets.Add(new SyncTarget(
                opts.AgentsDir ?? Path.Combine(home, ".agents", "skills"),
                SkillRenderFlavor.AgentsStandard, RepoRoot: null));
        }
        else
        {
            // In-repo rendering is opt-in via --repo-dir and carries git safety checks.
            var root = Path.GetFullPath(opts.RepoDir);
            targets.Add(new SyncTarget(Path.Combine(root, ".claude", "skills"), SkillRenderFlavor.ClaudeCode, root));
            targets.Add(new SyncTarget(Path.Combine(root, ".agents", "skills"), SkillRenderFlavor.AgentsStandard, root));
        }

        return targets;
    }

    /// <summary>Git-leak defenses for in-repo targets: exclude from git, warn on GitHub remotes, refuse dirty trees.</summary>
    private static bool EnsureRepoTargetSafe(SyncTarget target, SyncOptions opts)
    {
        var root = target.RepoRoot!;
        if (!Directory.Exists(Path.Combine(root, ".git")))
        {
            return true; // not a git repo — nothing to defend
        }

        var (remoteExit, remotes) = RunGit(root, "remote -v");
        if (remoteExit == 0 && remotes.Contains("github.com", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine($"  ⚠ {root} has a GitHub remote — synced skills would be one 'git add -A' away from being pushed.");
            Console.WriteLine("    They are appended to .git/info/exclude, but double-check before committing skill dirs.");
        }

        var relTarget = Path.GetRelativePath(root, target.Directory).Replace('\\', '/');
        var (statusExit, status) = RunGit(root, $"status --porcelain -- \"{relTarget}\"");
        if (statusExit == 0 && !string.IsNullOrWhiteSpace(status) && !opts.ForceServer)
        {
            Console.WriteLine($"  ✗ {relTarget} has uncommitted changes — refusing to sync into a dirty path (use --force-server to override).");
            return false;
        }

        if (!opts.DryRun)
        {
            AppendGitExclude(root, relTarget);
        }
        return true;
    }

    private static void AppendGitExclude(string repoRoot, string relTarget)
    {
        try
        {
            var excludePath = Path.Combine(repoRoot, ".git", "info", "exclude");
            Directory.CreateDirectory(Path.GetDirectoryName(excludePath)!);
            var line = $"/{relTarget}/";
            var existing = File.Exists(excludePath) ? File.ReadAllLines(excludePath) : [];
            if (!existing.Contains(line, StringComparer.Ordinal))
            {
                File.AppendAllLines(excludePath, [line]);
            }
        }
        catch (IOException)
        {
            // Non-fatal: exclusion is a convenience; the warning above still fired.
        }
    }

    private static (int ExitCode, string Output) RunGit(string workingDir, string arguments)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "git",
                Arguments = arguments,
                WorkingDirectory = workingDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            });
            if (process is null)
            {
                return (-1, "");
            }
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(10_000);
            return (process.ExitCode, output);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return (-1, ""); // git not installed — skip git safety, plain file sync still applies
        }
    }

    private static Dictionary<string, string> CollectFileHashes(
        string directory, List<RenderedSkill> rendered, SkillSyncManifest manifest)
    {
        var relPaths = rendered.Select(r => SkillSyncPlanner.RelativePath(r.Name))
            .Concat(manifest.Entries.Select(e => e.RelativePath))
            .Distinct(StringComparer.Ordinal);

        var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var relPath in relPaths)
        {
            var fullPath = Path.Combine(directory, relPath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(fullPath))
            {
                hashes[relPath] = SkillSyncPlanner.ComputeHash(File.ReadAllText(fullPath));
            }
        }
        return hashes;
    }

    // ---------------------------------------------------------------- init-dirs

    private static int InitDirs(string[] args)
    {
        string? repoDir = null;
        for (var i = 0; i < args.Length - 1; i += 2)
        {
            if (args[i] == "--repo-dir") repoDir = args[i + 1];
            else throw new ArgumentException($"Unknown flag '{args[i]}'.");
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var dirs = new List<string>
        {
            Path.Combine(home, ".claude", "skills"),
            Path.Combine(home, ".agents", "skills"),
        };
        if (repoDir is not null)
        {
            dirs.Add(Path.Combine(Path.GetFullPath(repoDir), ".claude", "skills"));
            dirs.Add(Path.Combine(Path.GetFullPath(repoDir), ".agents", "skills"));
        }

        foreach (var dir in dirs)
        {
            Directory.CreateDirectory(dir);
            Console.WriteLine($"  ✓ {dir}");
        }

        Console.WriteLine();
        Console.WriteLine("Note: Claude Code only watches skill directories that existed at session start —");
        Console.WriteLine("restart any running session once so the new directories are picked up.");
        return 0;
    }

    // ---------------------------------------------------------------- list

    private static async Task<int> ListAsync(string[] args)
    {
        var opts = ParseSyncArgs(args);
        var skills = await LoadSkillRowsAsync(opts, publishedOnly: false).ConfigureAwait(false);
        if (skills.Count == 0)
        {
            Console.WriteLine("No skills in this project.");
            return 0;
        }

        Console.WriteLine($"{"NAME",-40} {"STATUS",-11} {"VER",4} {"+",4} {"-",4} {"USES",5}  UPDATED");
        foreach (var row in skills)
        {
            Console.WriteLine($"{row.Skill.Name,-40} {row.Skill.Status,-11} {row.Skill.CurrentVersion,4} {row.Skill.HelpfulCount,4} {row.Skill.HarmfulCount,4} {row.Skill.UsageCount,5}  {row.Skill.UpdatedAt:yyyy-MM-dd}");
        }
        return 0;
    }

    // ---------------------------------------------------------------- harvest

    private static async Task<int> HarvestAsync(string[] args)
    {
        var opts = ParseHarvestArgs(args);

        string transcript;
        if (opts.TranscriptPath is not null)
        {
            if (!File.Exists(opts.TranscriptPath))
            {
                return Fail($"transcript not found: {opts.TranscriptPath}");
            }
            transcript = await File.ReadAllTextAsync(opts.TranscriptPath).ConfigureAwait(false);
        }
        else
        {
            using var reader = new StreamReader(Console.OpenStandardInput());
            transcript = await reader.ReadToEndAsync().ConfigureAwait(false);
        }

        if (string.IsNullOrWhiteSpace(transcript))
        {
            return Fail("transcript is empty — nothing to harvest.");
        }
        if (transcript.Length > HarvestIntake.MaxTranscriptBytes)
        {
            return Fail($"transcript exceeds {HarvestIntake.MaxTranscriptBytes / (1024 * 1024)} MB cap.");
        }

        var payload = HarvestIntake.Prepare(transcript);
        var statsJson = System.Text.Json.JsonSerializer.Serialize(new
        {
            lines = payload.Lines,
            chars = payload.Chars,
            redactions = payload.Redactions,
            cwd = opts.Cwd,
            gitBranch = opts.GitBranch,
        });

        await using var conn = new NpgsqlConnection(opts.ConnectionString);
        await conn.OpenAsync().ConfigureAwait(false);
        await using (var setCmd = conn.CreateCommand())
        {
            setCmd.CommandText = $"SET app.organization_id = '{opts.OrganizationId:D}'; SET app.project_id = '{opts.ProjectId:D}';";
            await setCmd.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        await using var insertCmd = conn.CreateCommand();
        insertCmd.CommandText = """
            INSERT INTO memory.harvested_sessions
                (id, project_id, source, session_id, content_hash, transcript_gzip, status, stats, submitted_at)
            VALUES (@id, @project, @source, @session, @hash, @gz, 0, @stats::jsonb, now())
            ON CONFLICT (project_id, content_hash) DO NOTHING
            """;
        insertCmd.Parameters.AddWithValue("id", Guid.NewGuid());
        insertCmd.Parameters.AddWithValue("project", opts.ProjectId);
        insertCmd.Parameters.AddWithValue("source", opts.Source);
        insertCmd.Parameters.AddWithValue("session", opts.SessionId);
        insertCmd.Parameters.AddWithValue("hash", payload.ContentHash);
        insertCmd.Parameters.AddWithValue("gz", payload.TranscriptGzip);
        insertCmd.Parameters.AddWithValue("stats", statsJson);

        var inserted = await insertCmd.ExecuteNonQueryAsync().ConfigureAwait(false);
        if (!opts.Quiet)
        {
            Console.WriteLine(inserted == 1
                ? $"harvested: {payload.Lines} lines, {payload.Redactions} redaction(s), hash {payload.ContentHash[..12]}…"
                : "duplicate transcript — already harvested (safe to ignore).");
        }
        return 0;
    }

    private static HarvestOptions ParseHarvestArgs(string[] args)
    {
        var connStr = Environment.GetEnvironmentVariable("MEMORY_CONNSTR");
        Guid? org = null, project = null;
        string source = "claude-code", sessionId = "unknown";
        string? path = null, cwd = null, gitBranch = null;
        var stdin = false; var quiet = false;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--stdin": stdin = true; continue;
                case "--quiet": quiet = true; continue;
            }

            if (i == args.Length - 1)
            {
                throw new ArgumentException($"Flag '{args[i]}' expects a value.");
            }

            var value = args[++i];
            switch (args[i - 1])
            {
                case "--connection-string": connStr = value; break;
                case "--org": org = Guid.Parse(value); break;
                case "--project": project = Guid.Parse(value); break;
                case "--source": source = value; break;
                case "--session-id": sessionId = value; break;
                case "--path": path = value; break;
                case "--cwd": cwd = value; break;
                case "--git-branch": gitBranch = value; break;
                default: throw new ArgumentException($"Unknown flag '{args[i - 1]}'. Run 'memory skills help'.");
            }
        }

        if (string.IsNullOrWhiteSpace(connStr))
        {
            throw new ArgumentException("Connection string required: --connection-string or MEMORY_CONNSTR.");
        }
        if (org is null || project is null)
        {
            throw new ArgumentException("--org and --project (tenant GUIDs) are required.");
        }
        if (path is null && !stdin)
        {
            throw new ArgumentException("Provide --path <transcript.jsonl> or --stdin.");
        }

        return new HarvestOptions(connStr, org.Value, project.Value, source, sessionId, path, cwd, gitBranch, quiet);
    }

    private sealed record HarvestOptions(
        string ConnectionString,
        Guid OrganizationId,
        Guid ProjectId,
        string Source,
        string SessionId,
        string? TranscriptPath,
        string? Cwd,
        string? GitBranch,
        bool Quiet);

    // ---------------------------------------------------------------- data access

    private static async Task<List<Skill>> LoadPublishedSkillsAsync(SyncOptions opts)
    {
        var rows = await LoadSkillRowsAsync(opts, publishedOnly: true).ConfigureAwait(false);
        return rows.Select(r => r.Skill).ToList();
    }

    private static async Task<List<(Skill Skill, string _)>> LoadSkillRowsAsync(SyncOptions opts, bool publishedOnly)
    {
        await using var conn = new NpgsqlConnection(opts.ConnectionString);
        await conn.OpenAsync().ConfigureAwait(false);

        // RLS: same GUC dance every runtime connection does.
        await using (var setCmd = conn.CreateCommand())
        {
            setCmd.CommandText = $"SET app.organization_id = '{opts.OrganizationId:D}'; SET app.project_id = '{opts.ProjectId:D}';";
            await setCmd.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT name, description, when_to_use, body, frontmatter_extra,
                   status, origin, current_version, helpful_count, harmful_count,
                   usage_count, created_at, updated_at
            FROM memory.skills
            {(publishedOnly ? "WHERE status = 2" : "")}
            ORDER BY name
            """;

        var result = new List<(Skill, string)>();
        await using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            var skill = new Skill
            {
                Id = SkillId.New(), // identity not needed for rendering
                Project = new ProjectId(opts.ProjectId),
                Name = reader.GetString(0),
                Description = reader.GetString(1),
                WhenToUse = reader.IsDBNull(2) ? null : reader.GetString(2),
                Body = reader.GetString(3),
                FrontmatterExtraJson = reader.IsDBNull(4) ? null : reader.GetString(4),
                Status = (SkillStatus)reader.GetInt16(5),
                Origin = (SkillOrigin)reader.GetInt16(6),
                CurrentVersion = reader.GetInt32(7),
                HelpfulCount = reader.GetInt32(8),
                HarmfulCount = reader.GetInt32(9),
                UsageCount = reader.GetInt32(10),
                CreatedAt = reader.GetFieldValue<DateTimeOffset>(11),
                UpdatedAt = reader.GetFieldValue<DateTimeOffset>(12),
            };
            result.Add((skill, skill.Name));
        }
        return result;
    }

    // ---------------------------------------------------------------- args

    private static SyncOptions ParseSyncArgs(string[] args)
    {
        var connStr = Environment.GetEnvironmentVariable("MEMORY_CONNSTR");
        Guid? org = null, project = null;
        string? claudeDir = null, agentsDir = null, repoDir = null;
        var forceServer = false; var dryRun = false; var quiet = false;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--force-server": forceServer = true; continue;
                case "--dry-run": dryRun = true; continue;
                case "--quiet": quiet = true; continue;
            }

            if (i == args.Length - 1)
            {
                throw new ArgumentException($"Flag '{args[i]}' expects a value.");
            }

            var value = args[++i];
            switch (args[i - 1])
            {
                case "--connection-string": connStr = value; break;
                case "--org": org = Guid.Parse(value); break;
                case "--project": project = Guid.Parse(value); break;
                case "--claude-dir": claudeDir = value; break;
                case "--agents-dir": agentsDir = value; break;
                case "--repo-dir": repoDir = value; break;
                default: throw new ArgumentException($"Unknown flag '{args[i - 1]}'. Run 'memory skills help'.");
            }
        }

        if (string.IsNullOrWhiteSpace(connStr))
        {
            throw new ArgumentException("Connection string required: --connection-string or MEMORY_CONNSTR.");
        }
        if (org is null || project is null)
        {
            throw new ArgumentException("--org and --project (tenant GUIDs) are required.");
        }

        return new SyncOptions(connStr, org.Value, project.Value, claudeDir, agentsDir, repoDir, forceServer, dryRun, quiet);
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine($"error: {message}");
        return 1;
    }

    private static void PrintHelp() => Console.WriteLine(
        """
        memory skills — render published skills into agent skill directories.

        Subcommands:
          sync        Render published skills to ~/.claude/skills (Claude Code flavor)
                      and ~/.agents/skills (open Agent Skills standard: Codex, Cursor,
                      Gemini CLI, Copilot). Ownership manifest (.llm-memory-sync.json)
                      guarantees hand-written skills are never touched.
                        --connection-string <cs>   or MEMORY_CONNSTR
                        --org <guid> --project <guid>   tenant scope (required)
                        --claude-dir <path>        override Claude Code target
                        --agents-dir <path>        override .agents target
                        --repo-dir <path>          opt-in in-repo render (git safety:
                                                   .git/info/exclude, GitHub-remote
                                                   warning, dirty-tree refusal)
                        --force-server             overwrite locally-modified files
                        --dry-run                  plan only, write nothing
                        --quiet                    errors/conflicts only
          init-dirs   Pre-create the skill directories (Claude Code only watches
                      directories that existed at session start).
                        --repo-dir <path>          also create in-repo dirs
          list        List all skills in the project with status and counters.
                      Same connection/tenant flags as sync.
          harvest     Submit an agent session transcript into the synthesis inbox
                      (secrets are redacted before storage; duplicates dedup by
                      content hash, so hook double-fires are safe).
                        --path <transcript.jsonl> | --stdin
                        --source claude-code|codex   (default claude-code)
                        --session-id <id>
                        --cwd <path> --git-branch <name>   optional stats
                        + the same connection/tenant flags as sync
        """);

    private sealed record SyncOptions(
        string ConnectionString,
        Guid OrganizationId,
        Guid ProjectId,
        string? ClaudeDir,
        string? AgentsDir,
        string? RepoDir,
        bool ForceServer,
        bool DryRun,
        bool Quiet);

    private sealed record SyncTarget(string Directory, SkillRenderFlavor Flavor, string? RepoRoot);
}
