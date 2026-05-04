using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Memory.Storage;

internal sealed class MemoryDesignTimeDbContextFactory : IDesignTimeDbContextFactory<MemoryDbContext>
{
    public MemoryDbContext CreateDbContext(string[] args)
    {
        var connectionString =
            Environment.GetEnvironmentVariable("MEMORY_DESIGN_CONNSTR")
            ?? "Host=localhost;Port=5432;Database=llm_memory_design;Username=postgres;Password=postgres";

        var options = new DbContextOptionsBuilder<MemoryDbContext>()
            .UseNpgsql(connectionString, npg => npg.UseVector())
            .Options;

        return new MemoryDbContext(options);
    }
}
