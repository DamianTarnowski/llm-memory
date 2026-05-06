using Memory.Domain;

namespace Memory.Api;

/// <summary>
/// Endpoint filter that rejects requests whose authenticated <see cref="ApiKey"/>
/// does not have <c>IsAdmin = true</c>. Returns <c>401</c> when no key is on the
/// context (not authenticated), <c>403</c> when authenticated but not admin.
/// Apply via <c>MapGroup(...).AddEndpointFilter&lt;AdminOnlyEndpointFilter&gt;()</c>.
/// </summary>
internal sealed class AdminOnlyEndpointFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext ctx, EndpointFilterDelegate next)
    {
        var key = ctx.HttpContext.Items[ApiKeyAuthMiddleware.ApiKeyContextKey] as ApiKey;
        if (key is null)
        {
            return Results.Json(
                new { error = "admin api key required", reason = "no bearer token resolved" },
                statusCode: StatusCodes.Status401Unauthorized);
        }
        if (!key.IsAdmin)
        {
            return Results.Json(
                new { error = "admin api key required", reason = "key.is_admin = false" },
                statusCode: StatusCodes.Status403Forbidden);
        }
        return await next(ctx).ConfigureAwait(false);
    }
}
