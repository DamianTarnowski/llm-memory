using Memory.Api;
using Memory.Domain;
using Microsoft.AspNetCore.Http;

namespace Memory.Api.Tests;

/// <summary>
/// The /api/secrets/* admin gate is the production-critical change from the
/// pre-public-release security pass. These tests exercise its three branches
/// without spinning up a webapp: a stub HttpContext is enough.
/// </summary>
public sealed class AdminOnlyEndpointFilterTests
{
    [Fact]
    public async Task Returns_401_when_no_authenticated_key_on_context()
    {
        var filter = new AdminOnlyEndpointFilter();
        var ctx = NewContext(apiKey: null);
        var nextCalled = false;

        var result = await filter.InvokeAsync(ctx, _ => { nextCalled = true; return ValueTask.FromResult<object?>(Results.Ok()); });

        Assert.False(nextCalled);
        var status = AssertStatus(result);
        Assert.Equal(StatusCodes.Status401Unauthorized, status);
    }

    [Fact]
    public async Task Returns_403_when_authenticated_key_is_not_admin()
    {
        var filter = new AdminOnlyEndpointFilter();
        var ctx = NewContext(apiKey: NewApiKey(isAdmin: false));
        var nextCalled = false;

        var result = await filter.InvokeAsync(ctx, _ => { nextCalled = true; return ValueTask.FromResult<object?>(Results.Ok()); });

        Assert.False(nextCalled);
        var status = AssertStatus(result);
        Assert.Equal(StatusCodes.Status403Forbidden, status);
    }

    [Fact]
    public async Task Calls_next_when_key_is_admin()
    {
        var filter = new AdminOnlyEndpointFilter();
        var ctx = NewContext(apiKey: NewApiKey(isAdmin: true));
        var nextCalled = false;
        var sentinel = Results.Ok(new { passed = true });

        var result = await filter.InvokeAsync(ctx, _ => { nextCalled = true; return ValueTask.FromResult<object?>(sentinel); });

        Assert.True(nextCalled);
        Assert.Same(sentinel, result);
    }

    [Fact]
    public async Task Returns_401_when_items_holds_unrelated_object()
    {
        // Defense in depth — if something else stuffs a non-ApiKey into the
        // dictionary at the same key, the filter must still refuse rather
        // than silently treat it as authenticated.
        var filter = new AdminOnlyEndpointFilter();
        var ctx = new DefaultHttpContext();
        ctx.Items[ApiKeyAuthMiddleware.ApiKeyContextKey] = "this is not an ApiKey";

        var result = await filter.InvokeAsync(NewInvocationContext(ctx), _ => ValueTask.FromResult<object?>(Results.Ok()));

        var status = AssertStatus(result);
        Assert.Equal(StatusCodes.Status401Unauthorized, status);
    }

    private static EndpointFilterInvocationContext NewContext(ApiKey? apiKey)
    {
        var http = new DefaultHttpContext();
        if (apiKey is not null)
        {
            http.Items[ApiKeyAuthMiddleware.ApiKeyContextKey] = apiKey;
        }
        return NewInvocationContext(http);
    }

    private static EndpointFilterInvocationContext NewInvocationContext(HttpContext http) =>
        new TestInvocationContext(http);

    private static ApiKey NewApiKey(bool isAdmin) => new()
    {
        Id = Guid.NewGuid(),
        KeyHash = new string('a', 64),
        Organization = new OrganizationId(Guid.NewGuid()),
        Project = new ProjectId(Guid.NewGuid()),
        CreatedByUser = new UserId(Guid.NewGuid()),
        Name = "test",
        CreatedAt = DateTimeOffset.UtcNow,
        IsAdmin = isAdmin,
    };

    private static int AssertStatus(object? result)
    {
        var statusCodeResult = Assert.IsAssignableFrom<IStatusCodeHttpResult>(result);
        return statusCodeResult.StatusCode ?? 0;
    }

    private sealed class TestInvocationContext(HttpContext http) : EndpointFilterInvocationContext
    {
        public override HttpContext HttpContext { get; } = http;
        public override IList<object?> Arguments { get; } = new List<object?>();
        public override T GetArgument<T>(int index) => throw new NotImplementedException();
    }
}
