namespace Memory.Pipeline.Reflection;

public sealed class ReflectionScheduleOptions
{
    public const string SectionName = "ReflectionSchedule";

    public bool Enabled { get; set; } = false;
    public TimeSpan InitialDelay { get; set; } = TimeSpan.FromMinutes(5);
    public TimeSpan Interval { get; set; } = TimeSpan.FromHours(6);
    public int MaxNotesPerRun { get; set; } = 30;
    public string Scope { get; set; } = "scheduled";

    /// <summary>
    /// Tenants the BG service runs reflection against on every interval.
    /// Background services have no request scope, so the AsyncLocal tenant
    /// context is never automatically populated — the schedule has to be told
    /// explicitly which (org, user, project) tuples to fan out to. An empty
    /// list means "no scheduled reflection," which is the safe default.
    /// </summary>
    public List<ReflectionTenant> Tenants { get; set; } = new();
}

public sealed class ReflectionTenant
{
    public Guid Organization { get; set; }
    public Guid User { get; set; }
    public Guid Project { get; set; }
}
