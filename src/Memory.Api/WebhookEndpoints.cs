using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Memory.Pipeline;

namespace Memory.Api;

/// <summary>
/// Webhook surface for ingesting external events as episodes. Two endpoints:
///   POST /api/webhooks/{name}   — generic; body's <c>content</c> field (or
///                                  the raw body when not JSON) becomes the
///                                  episode content. Source label = name.
///   POST /api/webhooks/slack    — Slack Events API shape; verifies the
///                                  X-Slack-Signature HMAC against
///                                  <c>WebhookConnector:SlackSigningSecret</c>;
///                                  flattens message events into episodes.
///
/// Both routes resolve tenant scope through the same middleware as the rest of
/// the API (bearer key OR X-Memory-* headers). For Slack you'd typically point
/// the URL at one specific tenant's API key.
/// </summary>
public static class WebhookEndpoints
{
    public static void MapWebhooks(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/webhooks/{name}", HandleGenericAsync);
        app.MapPost("/api/webhooks/slack", HandleSlackAsync);
    }

    private static async Task<IResult> HandleGenericAsync(
        string name,
        HttpRequest request,
        IIngestionPipeline pipeline,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 64)
        {
            return Results.BadRequest(new { error = "name must be 1-64 chars" });
        }

        using var reader = new StreamReader(request.Body);
        var raw = await reader.ReadToEndAsync(ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return Results.BadRequest(new { error = "empty body" });
        }

        // If the body parses as JSON with a "content" string, prefer that;
        // otherwise treat the whole body as content.
        string content = raw;
        Dictionary<string, string>? metadata = null;
        if (raw.TrimStart().StartsWith('{'))
        {
            try
            {
                using var doc = JsonDocument.Parse(raw);
                if (doc.RootElement.TryGetProperty("content", out var cEl) && cEl.ValueKind == JsonValueKind.String)
                {
                    content = cEl.GetString() ?? raw;
                    metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["webhook_raw"] = raw };
                }
            }
            catch (JsonException)
            {
                // Not JSON — fall through with raw as content.
            }
        }

        var result = await pipeline.IngestAsync(new IngestionRequest(
            Source: $"webhook:{name}",
            Content: content,
            Metadata: metadata),
            ct).ConfigureAwait(false);

        return Results.Ok(new
        {
            episodeId = result.EpisodeId?.Value,
            skipped = result.Skipped,
            skipReason = result.SkipReason,
            noteIds = result.Notes.Select(n => n.Value).ToArray(),
        });
    }

    private static async Task<IResult> HandleSlackAsync(
        HttpContext httpContext,
        IConfiguration configuration,
        IIngestionPipeline pipeline,
        CancellationToken ct)
    {
        // Read body once (need the raw bytes for HMAC).
        httpContext.Request.EnableBuffering();
        using var reader = new StreamReader(httpContext.Request.Body, leaveOpen: true);
        var body = await reader.ReadToEndAsync(ct).ConfigureAwait(false);
        httpContext.Request.Body.Position = 0;

        var signingSecret = configuration["WebhookConnector:SlackSigningSecret"];
        if (!string.IsNullOrEmpty(signingSecret))
        {
            // https://api.slack.com/authentication/verifying-requests-from-slack
            var ts = httpContext.Request.Headers["X-Slack-Request-Timestamp"].FirstOrDefault();
            var sig = httpContext.Request.Headers["X-Slack-Signature"].FirstOrDefault();
            if (string.IsNullOrEmpty(ts) || string.IsNullOrEmpty(sig))
            {
                return Results.Unauthorized();
            }
            if (!long.TryParse(ts, out var tsL) || Math.Abs(DateTimeOffset.UtcNow.ToUnixTimeSeconds() - tsL) > 300)
            {
                return Results.Unauthorized();
            }
            var basestring = $"v0:{ts}:{body}";
            using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(signingSecret));
            var computed = "v0=" + Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(basestring))).ToLowerInvariant();
            if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(computed), Encoding.UTF8.GetBytes(sig)))
            {
                return Results.Unauthorized();
            }
        }

        // URL verification handshake — Slack requires us to echo the challenge.
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.TryGetProperty("type", out var typeEl) && typeEl.GetString() == "url_verification")
            {
                var challenge = root.GetProperty("challenge").GetString() ?? "";
                return Results.Text(challenge);
            }

            // Event callback — extract message events; ignore everything else.
            if (root.TryGetProperty("type", out var t2) && t2.GetString() == "event_callback")
            {
                var ev = root.GetProperty("event");
                var evType = ev.GetProperty("type").GetString();
                if (evType is "message" or "app_mention")
                {
                    var text = ev.TryGetProperty("text", out var txt) ? txt.GetString() ?? "" : "";
                    if (string.IsNullOrWhiteSpace(text)) return Results.Ok();
                    var channel = ev.TryGetProperty("channel", out var ch) ? ch.GetString() : null;
                    var user = ev.TryGetProperty("user", out var u) ? u.GetString() : null;

                    var meta = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    if (channel is not null) meta["slack_channel"] = channel;
                    if (user is not null) meta["slack_user"] = user;

                    var result = await pipeline.IngestAsync(new IngestionRequest(
                        Source: "webhook:slack",
                        Content: text,
                        Metadata: meta),
                        ct).ConfigureAwait(false);
                    return Results.Ok(new
                    {
                        episodeId = result.EpisodeId?.Value,
                        skipped = result.Skipped,
                        skipReason = result.SkipReason,
                    });
                }
            }
            return Results.Ok();
        }
        catch (JsonException)
        {
            return Results.BadRequest(new { error = "invalid JSON" });
        }
    }
}
