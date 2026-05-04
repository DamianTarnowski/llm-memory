namespace Memory.Pipeline.Reflection;

public sealed class ReflectionScheduleOptions
{
    public const string SectionName = "ReflectionSchedule";

    public bool Enabled { get; set; } = false;
    public TimeSpan InitialDelay { get; set; } = TimeSpan.FromMinutes(5);
    public TimeSpan Interval { get; set; } = TimeSpan.FromHours(6);
    public int MaxNotesPerRun { get; set; } = 30;
    public string Scope { get; set; } = "scheduled";
}
