using System.Text.RegularExpressions;

namespace Memory.Domain;

/// <summary>
/// Pure regex-based secret redaction, run at harvest intake (before a transcript
/// row is stored) and again at the skill-draft gate. Pattern-based only — no
/// entropy heuristics in v1 (too many false positives on hashes/guids); patterns
/// cover the credential shapes this stack actually uses.
/// </summary>
public static partial class SecretScanner
{
    /// <summary>Ordered list — more specific patterns first so the redaction label is accurate.</summary>
    private static readonly (string Kind, Regex Pattern)[] _patterns =
    [
        ("pem-private-key", PemPrivateKeyRegex()),
        ("aws-access-key", AwsAccessKeyRegex()),
        ("github-token", GitHubTokenRegex()),
        ("slack-token", SlackTokenRegex()),
        ("google-api-key", GoogleApiKeyRegex()),
        ("google-oauth-token", GoogleOAuthRegex()),
        ("openai-key", OpenAiKeyRegex()),
        ("anthropic-key", AnthropicKeyRegex()),
        ("memory-api-key", MemoryApiKeyRegex()),
        ("jwt", JwtRegex()),
        ("connstring-password", ConnStringPasswordRegex()),
        ("assigned-secret", AssignedSecretRegex()),
        ("bearer-header", BearerHeaderRegex()),
    ];

    /// <summary>Redacts all recognized secrets; returns the redacted text and the number of replacements.</summary>
    public static (string Redacted, int Findings) Redact(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return (text, 0);
        }

        var findings = 0;
        foreach (var (kind, pattern) in _patterns)
        {
            text = pattern.Replace(text, m =>
            {
                findings++;
                // Keep any capture named "keep" (e.g. "Password=") so structure stays readable.
                var prefix = m.Groups["keep"].Success ? m.Groups["keep"].Value : string.Empty;
                return $"{prefix}[REDACTED:{kind}]";
            });
        }

        return (text, findings);
    }

    [GeneratedRegex(@"-----BEGIN [A-Z ]*PRIVATE KEY-----[\s\S]*?-----END [A-Z ]*PRIVATE KEY-----")]
    private static partial Regex PemPrivateKeyRegex();

    [GeneratedRegex(@"\bAKIA[0-9A-Z]{16}\b")]
    private static partial Regex AwsAccessKeyRegex();

    [GeneratedRegex(@"\b(?:ghp|gho|ghu|ghs|ghr)_[A-Za-z0-9]{36,}\b|\bgithub_pat_[A-Za-z0-9_]{22,}\b")]
    private static partial Regex GitHubTokenRegex();

    [GeneratedRegex(@"\bxox[baprs]-[A-Za-z0-9-]{10,}\b")]
    private static partial Regex SlackTokenRegex();

    [GeneratedRegex(@"\bAIza[0-9A-Za-z_\-]{35}\b")]
    private static partial Regex GoogleApiKeyRegex();

    [GeneratedRegex(@"\bya29\.[0-9A-Za-z_\-]{20,}\b")]
    private static partial Regex GoogleOAuthRegex();

    [GeneratedRegex(@"\bsk-(?:proj-|svcacct-|admin-)?[A-Za-z0-9_\-]{20,}\b")]
    private static partial Regex OpenAiKeyRegex();

    [GeneratedRegex(@"\bsk-ant-[A-Za-z0-9_\-]{20,}\b")]
    private static partial Regex AnthropicKeyRegex();

    [GeneratedRegex(@"\bmemk_[A-Za-z0-9_\-]{20,}\b")]
    private static partial Regex MemoryApiKeyRegex();

    [GeneratedRegex(@"\beyJ[A-Za-z0-9_\-]{10,}\.eyJ[A-Za-z0-9_\-]{10,}\.[A-Za-z0-9_\-]{10,}\b")]
    private static partial Regex JwtRegex();

    [GeneratedRegex(@"(?<keep>(?:Password|Pwd)\s*=\s*)[^;\s""']+", RegexOptions.IgnoreCase)]
    private static partial Regex ConnStringPasswordRegex();

    [GeneratedRegex(
        """(?<keep>(?:api[_-]?key|apikey|secret|token|password|passwd|client[_-]?secret|access[_-]?token)["']?\s*[:=]\s*["'])[A-Za-z0-9_\-\./\+=]{12,}""",
        RegexOptions.IgnoreCase)]
    private static partial Regex AssignedSecretRegex();

    [GeneratedRegex(@"(?<keep>Authorization:\s*Bearer\s+)[A-Za-z0-9_\-\.=/\+]{16,}", RegexOptions.IgnoreCase)]
    private static partial Regex BearerHeaderRegex();
}
