namespace Memory.Web;

public sealed class TenantSettings
{
    public string ApiBaseUrl { get; set; } = "http://localhost:5566/";
    public Guid OrganizationId { get; set; } = Guid.Parse("90eb678a-e86d-47d0-897c-9f5918952d8b");
    public Guid UserId { get; set; } = Guid.Parse("2361fe5e-09d1-4c1d-b316-dab99acb9aba");
    public Guid ProjectId { get; set; } = Guid.Parse("b675f6dd-8aba-4f2a-8ab9-1cb40fa5529a");
}

internal sealed class TenantHeaderHandler(TenantSettings tenant) : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        request.Headers.TryAddWithoutValidation("X-Memory-Org-Id", tenant.OrganizationId.ToString("D"));
        request.Headers.TryAddWithoutValidation("X-Memory-User-Id", tenant.UserId.ToString("D"));
        request.Headers.TryAddWithoutValidation("X-Memory-Project-Id", tenant.ProjectId.ToString("D"));
        return base.SendAsync(request, cancellationToken);
    }
}
