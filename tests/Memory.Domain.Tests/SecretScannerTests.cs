namespace Memory.Domain.Tests;

public class SecretScannerTests
{
    // Token fixtures are BUILT AT RUNTIME from parts so that (a) no scanner-matching
    // string ever appears verbatim in the source/repo and (b) nothing can be derived
    // from a real credential. AKIAIOSFODNN7EXAMPLE is AWS's official docs example key.
    public static TheoryData<string, string> TokenShapes() => new()
    {
        { "key AKIAIOSFODNN7" + "EXAMPLE here", "aws-access-key" },
        { "token ghp_" + new string('a', 26) + "0123456789 ok", "github-token" },
        { "pat github_pat_" + "11FAKE" + new string('0', 16) + "_x more", "github-token" },
        { "slack xoxb-" + "1234567890-" + new string('a', 10) + " done", "slack-token" },
        { "google AIza" + "Sy" + new string('f', 33) + " x", "google-api-key" },
        { "bearer memk_" + new string('f', 24) + " end", "memory-api-key" },
        { "openai sk-proj-" + new string('f', 26) + " end", "openai-key" },
    };

    [Theory]
    [MemberData(nameof(TokenShapes))]
    public void Redact_ReplacesKnownTokenShapes(string input, string expectedKind)
    {
        var (redacted, findings) = SecretScanner.Redact(input);
        Assert.True(findings >= 1);
        Assert.Contains($"[REDACTED:{expectedKind}]", redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void Redact_ConnectionStringPassword_KeepsKeyName()
    {
        var (redacted, findings) = SecretScanner.Redact(
            "Host=localhost;Port=5435;Database=llm_memory;Username=memory_app;Password=s3cr3t-value;Timeout=5");
        Assert.Equal(1, findings);
        Assert.Contains("Password=[REDACTED:connstring-password]", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("s3cr3t-value", redacted, StringComparison.Ordinal);
        Assert.Contains("Username=memory_app", redacted, StringComparison.Ordinal); // non-secret parts intact
    }

    [Fact]
    public void Redact_JsonAssignedSecret_KeepsKeyAndQuote()
    {
        var (redacted, findings) = SecretScanner.Redact(
            """{"ApiKey": "abcdef1234567890abcdef", "Model": "gpt-5-mini"}""");
        Assert.Equal(1, findings);
        Assert.Contains("\"ApiKey\": \"[REDACTED:assigned-secret]", redacted, StringComparison.Ordinal);
        Assert.Contains("gpt-5-mini", redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void Redact_PemBlock_RemovedEntirely()
    {
        var pem = "-----BEGIN RSA PRIVATE KEY-----\nMIIEow...\nxyz\n-----END RSA PRIVATE KEY-----";
        var (redacted, _) = SecretScanner.Redact($"before\n{pem}\nafter");
        Assert.DoesNotContain("MIIEow", redacted, StringComparison.Ordinal);
        Assert.Contains("[REDACTED:pem-private-key]", redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void Redact_Jwt_Replaced()
    {
        var jwt = "eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.dozjgNryP4J3jVmNHl0w5N_XgL0n3I9PlFUP0THsR8U";
        var (redacted, findings) = SecretScanner.Redact($"header {jwt} trailer");
        Assert.Equal(1, findings);
        Assert.Contains("[REDACTED:jwt]", redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void Redact_BearerHeader_KeepsHeaderName()
    {
        var (redacted, _) = SecretScanner.Redact("Authorization: Bearer abcdefghijklmnop123456");
        Assert.StartsWith("Authorization: Bearer [REDACTED:", redacted, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("plain text with no secrets at all")]
    [InlineData("guid 90eb678a-e86d-47d0-897c-9f5918952d8b is fine")]
    [InlineData("sha 4c7b53bf6657605e0c55349b0bb6cf9f69736bb0d29e2fee14b9fb49e3db726c ok")]
    [InlineData("dotnet build src/Memory.Api -v q")]
    public void Redact_LeavesInnocentTextAlone(string input)
    {
        var (redacted, findings) = SecretScanner.Redact(input);
        Assert.Equal(0, findings);
        Assert.Equal(input, redacted);
    }

    [Fact]
    public void Redact_EmptyString_NoOp()
    {
        var (redacted, findings) = SecretScanner.Redact("");
        Assert.Equal("", redacted);
        Assert.Equal(0, findings);
    }
}
