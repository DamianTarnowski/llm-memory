using Memory.Api;

namespace Memory.Api.Tests;

/// <summary>
/// The bearer-token hashing function is the load-bearing piece of API-key
/// auth: a wrong implementation either silently fails to authenticate
/// real keys or — much worse — collides legitimate keys with each other.
/// These tests pin the contract: SHA-256, lowercase hex, deterministic,
/// distinct inputs produce distinct outputs.
/// </summary>
public sealed class ApiKeyHashingTests
{
    [Fact]
    public void HashToken_is_deterministic()
    {
        var a = ApiKeyAuthMiddleware.HashToken("memk_abc123");
        var b = ApiKeyAuthMiddleware.HashToken("memk_abc123");

        Assert.Equal(a, b);
    }

    [Fact]
    public void HashToken_returns_64_lowercase_hex_chars()
    {
        var hash = ApiKeyAuthMiddleware.HashToken("memk_abc123");

        Assert.Equal(64, hash.Length);
        Assert.Matches("^[0-9a-f]{64}$", hash);
    }

    [Fact]
    public void HashToken_distinguishes_distinct_inputs()
    {
        var a = ApiKeyAuthMiddleware.HashToken("memk_abc");
        var b = ApiKeyAuthMiddleware.HashToken("memk_xyz");

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void HashToken_is_case_sensitive()
    {
        // API keys are opaque random bytes — case folding would shrink the
        // search space and is not what the auth middleware does in production.
        var a = ApiKeyAuthMiddleware.HashToken("memk_AbCdEf");
        var b = ApiKeyAuthMiddleware.HashToken("memk_abcdef");

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void HashToken_handles_unicode()
    {
        // Tokens are ASCII-by-construction (base64 alphabet) but the hasher
        // must not crash if someone manages to feed a non-ASCII string in.
        var hash = ApiKeyAuthMiddleware.HashToken("memk_źółć");

        Assert.Equal(64, hash.Length);
    }

    [Fact]
    public void HashToken_handles_empty_string()
    {
        // Well-known SHA-256 of empty string.
        var hash = ApiKeyAuthMiddleware.HashToken("");

        Assert.Equal("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855", hash);
    }
}
