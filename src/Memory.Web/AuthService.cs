using Microsoft.JSInterop;

namespace Memory.Web;

/// <summary>
/// Holds the active bearer token in memory + persists it to localStorage so it
/// survives page reloads. Used by <see cref="BearerTokenHandler"/> to attach
/// <c>Authorization: Bearer &lt;token&gt;</c> to every <see cref="ApiClient"/>
/// call. When the token is null/empty the request falls through to the legacy
/// <c>X-Memory-*</c> tenant headers, preserving the dev-time behavior where no
/// token is configured.
/// </summary>
public sealed class AuthService(IJSRuntime js)
{
    private const string Key = "memory.web.token";
    private string? _token;
    public string? Token => _token;
    public bool IsAuthenticated => !string.IsNullOrEmpty(_token);

    public event Action? OnChanged;

    public async Task LoadAsync()
    {
        try
        {
            _token = await js.InvokeAsync<string?>("localStorage.getItem", Key);
        }
        catch
        {
            _token = null;
        }
    }

    public async Task SetAsync(string? token)
    {
        _token = string.IsNullOrWhiteSpace(token) ? null : token.Trim();
        try
        {
            if (_token is null) await js.InvokeVoidAsync("localStorage.removeItem", Key);
            else await js.InvokeVoidAsync("localStorage.setItem", Key, _token);
        }
        catch { /* localStorage unavailable (private mode etc) — keep in-memory only */ }
        OnChanged?.Invoke();
    }
}

internal sealed class BearerTokenHandler(AuthService auth, TenantSettings tenant) : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (auth.IsAuthenticated)
        {
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", auth.Token!);
        }
        else
        {
            // Dev fallback — when no token is set, send the configured X-Memory-* headers
            // so an unconfigured local install keeps working out of the box.
            request.Headers.TryAddWithoutValidation("X-Memory-Org-Id", tenant.OrganizationId.ToString("D"));
            request.Headers.TryAddWithoutValidation("X-Memory-User-Id", tenant.UserId.ToString("D"));
            request.Headers.TryAddWithoutValidation("X-Memory-Project-Id", tenant.ProjectId.ToString("D"));
        }
        return base.SendAsync(request, cancellationToken);
    }
}
