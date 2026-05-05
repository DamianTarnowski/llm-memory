namespace Memory.Web;

/// <summary>
/// Backwards-compat dev settings. Used by <see cref="BearerTokenHandler"/> as a
/// fallback when no API key is configured via the Login page — sends X-Memory-*
/// headers with the values below. In any non-dev deployment, set the API key
/// through the /login page; the GUIDs here become irrelevant.
/// </summary>
public sealed class TenantSettings
{
    public string ApiBaseUrl { get; set; } = "http://localhost:5566/";
    public Guid OrganizationId { get; set; } = Guid.Parse("90eb678a-e86d-47d0-897c-9f5918952d8b");
    public Guid UserId { get; set; } = Guid.Parse("2361fe5e-09d1-4c1d-b316-dab99acb9aba");
    public Guid ProjectId { get; set; } = Guid.Parse("b675f6dd-8aba-4f2a-8ab9-1cb40fa5529a");
}
