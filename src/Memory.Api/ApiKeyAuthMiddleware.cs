using System.Security.Cryptography;
using System.Text;
using Memory.Domain;
using Memory.Storage;
using Memory.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Memory.Api;

/// <summary>
/// Resolves a tenant scope from an Authorization: Bearer &lt;api-key&gt; header.
/// Hashes the bearer token (SHA-256) and looks it up in memory.api_keys; if active,
/// pushes the key's (org, user, project) onto AmbientTenantContext for the request.
/// Falls through to the next middleware if no header / unknown / revoked key —
/// the TenantHeaderMiddleware can still resolve scope from X-Memory-* headers in
/// that case (useful for dev).
/// </summary>
internal sealed class ApiKeyAuthMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, ITenantContext tenant, MemoryDbContext db)
    {
        var token = ExtractBearer(context.Request.Headers.Authorization.ToString());
        if (token is null)
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        var hash = HashToken(token);
        var key = await db.ApiKeys
            .Where(k => k.KeyHash == hash && k.RevokedAt == null)
            .FirstOrDefaultAsync(context.RequestAborted)
            .ConfigureAwait(false);

        if (key is null)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsync("Invalid or revoked API key.", context.RequestAborted).ConfigureAwait(false);
            return;
        }

        using var _ = tenant.BeginScope(new TenantScope(key.Organization, key.CreatedByUser, key.Project));
        try
        {
            await next(context).ConfigureAwait(false);
        }
        finally
        {
            // Best-effort last-used timestamp update; ignore failures so they don't tank the request.
            try
            {
                key.GetType(); // shut up the compiler about unused capture
                await db.ApiKeys
                    .Where(k => k.Id == key.Id)
                    .ExecuteUpdateAsync(s => s.SetProperty(k => k.LastUsedAt, _ => DateTimeOffset.UtcNow))
                    .ConfigureAwait(false);
            }
            catch { /* best effort */ }
        }
    }

    private static string? ExtractBearer(string authHeader)
    {
        if (string.IsNullOrWhiteSpace(authHeader)) return null;
        const string prefix = "Bearer ";
        if (!authHeader.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;
        var token = authHeader[prefix.Length..].Trim();
        return string.IsNullOrEmpty(token) ? null : token;
    }

    public static string HashToken(string token)
    {
        var bytes = Encoding.UTF8.GetBytes(token);
        var hash = SHA256.HashData(bytes);
        var sb = new StringBuilder(hash.Length * 2);
        foreach (var b in hash) sb.Append(b.ToString("x2"));
        return sb.ToString();
    }
}
