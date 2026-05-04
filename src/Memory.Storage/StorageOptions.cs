namespace Memory.Storage;

public sealed class StorageOptions
{
    public const string SectionName = "Storage";

    public string ConnectionString { get; set; } = string.Empty;
    public string GraphName { get; set; } = "memory_graph";
    public string Schema { get; set; } = "memory";
}
