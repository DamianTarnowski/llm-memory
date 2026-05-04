using Memory.Domain;
using Memory.Storage.Internal;
using Microsoft.EntityFrameworkCore;

namespace Memory.Storage;

public sealed class MemoryDbContext(DbContextOptions<MemoryDbContext> options) : DbContext(options)
{
    public DbSet<Organization> Organizations => Set<Organization>();
    public DbSet<User> Users => Set<User>();
    public DbSet<Membership> Memberships => Set<Membership>();
    public DbSet<Project> Projects => Set<Project>();

    public DbSet<Episode> Episodes => Set<Episode>();
    public DbSet<Note> Notes => Set<Note>();
    public DbSet<NoteEmbedding> NoteEmbeddings => Set<NoteEmbedding>();
    public DbSet<NoteEntityMention> NoteEntityMentions => Set<NoteEntityMention>();
    public DbSet<Reflection> Reflections => Set<Reflection>();

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        base.OnConfiguring(optionsBuilder);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.HasDefaultSchema("memory");
        modelBuilder.HasPostgresExtension("vector");

        modelBuilder.ApplyConfigurationsFromAssembly(typeof(MemoryDbContext).Assembly);
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        base.ConfigureConventions(configurationBuilder);

        configurationBuilder.Properties<OrganizationId>().HaveConversion<OrganizationIdConverter>();
        configurationBuilder.Properties<UserId>().HaveConversion<UserIdConverter>();
        configurationBuilder.Properties<ProjectId>().HaveConversion<ProjectIdConverter>();
        configurationBuilder.Properties<EpisodeId>().HaveConversion<EpisodeIdConverter>();
        configurationBuilder.Properties<NoteId>().HaveConversion<NoteIdConverter>();
        configurationBuilder.Properties<EntityId>().HaveConversion<EntityIdConverter>();
        configurationBuilder.Properties<EdgeId>().HaveConversion<EdgeIdConverter>();
        configurationBuilder.Properties<ReflectionId>().HaveConversion<ReflectionIdConverter>();
    }
}
