using System.Text.Json;
using Memory.Domain;
using Memory.Storage.Internal;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Memory.Storage.Configurations;

internal sealed class OrganizationConfiguration : IEntityTypeConfiguration<Organization>
{
    public void Configure(EntityTypeBuilder<Organization> b)
    {
        b.ToTable("organizations");
        b.HasKey(o => o.Id);
        b.Property(o => o.Id).HasColumnName("id");
        b.Property(o => o.Slug).HasColumnName("slug").HasMaxLength(100).IsRequired();
        b.Property(o => o.Name).HasColumnName("name").HasMaxLength(200).IsRequired();
        b.Property(o => o.CreatedAt).HasColumnName("created_at");
        b.HasIndex(o => o.Slug).IsUnique();
    }
}

internal sealed class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> b)
    {
        b.ToTable("users");
        b.HasKey(u => u.Id);
        b.Property(u => u.Id).HasColumnName("id");
        b.Property(u => u.Email).HasColumnName("email").HasMaxLength(320).IsRequired();
        b.Property(u => u.DisplayName).HasColumnName("display_name").HasMaxLength(200).IsRequired();
        b.Property(u => u.CreatedAt).HasColumnName("created_at");
        b.HasIndex(u => u.Email).IsUnique();
    }
}

internal sealed class MembershipConfiguration : IEntityTypeConfiguration<Membership>
{
    public void Configure(EntityTypeBuilder<Membership> b)
    {
        b.ToTable("memberships");
        b.HasKey(m => new { m.Organization, m.User });
        b.Property(m => m.Organization).HasColumnName("organization_id");
        b.Property(m => m.User).HasColumnName("user_id");
        b.Property(m => m.Role).HasColumnName("role").HasConversion<short>();
        b.Property(m => m.GrantedAt).HasColumnName("granted_at");

        b.HasOne<Organization>().WithMany()
            .HasForeignKey(m => m.Organization)
            .OnDelete(DeleteBehavior.Cascade);
        b.HasOne<User>().WithMany()
            .HasForeignKey(m => m.User)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class ProjectConfiguration : IEntityTypeConfiguration<Project>
{
    public void Configure(EntityTypeBuilder<Project> b)
    {
        b.ToTable("projects");
        b.HasKey(p => p.Id);
        b.Property(p => p.Id).HasColumnName("id");
        b.Property(p => p.Organization).HasColumnName("organization_id");
        b.Property(p => p.Slug).HasColumnName("slug").HasMaxLength(100).IsRequired();
        b.Property(p => p.Name).HasColumnName("name").HasMaxLength(200).IsRequired();
        b.Property(p => p.EmbeddingModel).HasColumnName("embedding_model").HasMaxLength(100).IsRequired();
        b.Property(p => p.CreatedAt).HasColumnName("created_at");
        b.HasIndex(p => new { p.Organization, p.Slug }).IsUnique();

        b.HasOne<Organization>().WithMany()
            .HasForeignKey(p => p.Organization)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class EpisodeConfiguration : IEntityTypeConfiguration<Episode>
{
    public void Configure(EntityTypeBuilder<Episode> b)
    {
        b.ToTable("episodes");
        b.HasKey(e => e.Id);
        b.Property(e => e.Id).HasColumnName("id");
        b.Property(e => e.Project).HasColumnName("project_id");
        b.Property(e => e.Source).HasColumnName("source").HasMaxLength(100).IsRequired();
        b.Property(e => e.Content).HasColumnName("content").IsRequired();
        b.Property(e => e.OccurredAt).HasColumnName("occurred_at");
        b.Property(e => e.IngestedAt).HasColumnName("ingested_at");
        b.Property(e => e.Metadata)
            .HasColumnName("metadata")
            .HasColumnType("jsonb")
            .HasConversion(JsonbDictionaryConverter.Instance);
        b.HasIndex(e => e.Project);
        b.HasIndex(e => e.IngestedAt).IsDescending();

        b.HasOne<Project>().WithMany()
            .HasForeignKey(e => e.Project)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class NoteConfiguration : IEntityTypeConfiguration<Note>
{
    public void Configure(EntityTypeBuilder<Note> b)
    {
        b.ToTable("notes");
        b.HasKey(n => n.Id);
        b.Property(n => n.Id).HasColumnName("id");
        b.Property(n => n.Project).HasColumnName("project_id");
        b.Property(n => n.SourceEpisode).HasColumnName("source_episode_id");
        b.Property(n => n.Content).HasColumnName("content").IsRequired();
        b.Property(n => n.ContextDescription).HasColumnName("context_description").IsRequired();
        b.Property(n => n.Keywords).HasColumnName("keywords").HasColumnType("text[]");
        b.Property(n => n.Tags).HasColumnName("tags").HasColumnType("text[]");
        b.Property(n => n.Kind).HasColumnName("kind").HasConversion<short>();
        b.Property(n => n.CreatedAt).HasColumnName("created_at");
        b.Property(n => n.SupersededAt).HasColumnName("superseded_at");
        b.HasIndex(n => n.Project);
        b.HasIndex(n => n.SourceEpisode);

        b.HasOne<Project>().WithMany()
            .HasForeignKey(n => n.Project)
            .OnDelete(DeleteBehavior.Cascade);
        b.HasOne<Episode>().WithMany()
            .HasForeignKey(n => n.SourceEpisode)
            .OnDelete(DeleteBehavior.SetNull);
    }
}

internal sealed class NoteEmbeddingConfiguration : IEntityTypeConfiguration<NoteEmbedding>
{
    public const int DefaultDimensions = 3072;

    public void Configure(EntityTypeBuilder<NoteEmbedding> b)
    {
        b.ToTable("note_embeddings");
        b.HasKey(e => e.NoteId);
        b.Property(e => e.NoteId).HasColumnName("note_id");
        b.Property(e => e.Project).HasColumnName("project_id");
        b.Property(e => e.EmbeddingModel).HasColumnName("embedding_model").HasMaxLength(100).IsRequired();
        b.Property(e => e.Dimensions).HasColumnName("dimensions");
        b.Property(e => e.Embedding)
            .HasColumnName("embedding")
            .HasColumnType($"vector({DefaultDimensions})")
            .HasConversion(new FloatArrayVectorConverter());
        b.Property(e => e.CreatedAt).HasColumnName("created_at");
        b.HasIndex(e => e.Project);

        b.HasOne<Note>().WithMany()
            .HasForeignKey(e => e.NoteId)
            .OnDelete(DeleteBehavior.Cascade);
        b.HasOne<Project>().WithMany()
            .HasForeignKey(e => e.Project)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class NoteEntityMentionConfiguration : IEntityTypeConfiguration<NoteEntityMention>
{
    public void Configure(EntityTypeBuilder<NoteEntityMention> b)
    {
        b.ToTable("note_entity_mentions");
        b.HasKey(m => new { m.NoteId, m.EntityId });
        b.Property(m => m.NoteId).HasColumnName("note_id");
        b.Property(m => m.EntityId).HasColumnName("entity_id");
        b.Property(m => m.Project).HasColumnName("project_id");
        b.Property(m => m.CreatedAt).HasColumnName("created_at");
        b.HasIndex(m => m.EntityId);
        b.HasIndex(m => m.Project);

        b.HasOne<Note>().WithMany()
            .HasForeignKey(m => m.NoteId)
            .OnDelete(DeleteBehavior.Cascade);
        b.HasOne<Project>().WithMany()
            .HasForeignKey(m => m.Project)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class NoteRelationConfiguration : IEntityTypeConfiguration<NoteRelation>
{
    public void Configure(EntityTypeBuilder<NoteRelation> b)
    {
        b.ToTable("note_relations");
        b.HasKey(r => new { r.NoteId, r.RelatedNoteId });
        b.Property(r => r.NoteId).HasColumnName("note_id");
        b.Property(r => r.RelatedNoteId).HasColumnName("related_note_id");
        b.Property(r => r.Project).HasColumnName("project_id");
        b.Property(r => r.RelationType).HasColumnName("relation_type").HasMaxLength(50).IsRequired();
        b.Property(r => r.Confidence).HasColumnName("confidence");
        b.Property(r => r.Similarity).HasColumnName("similarity");
        b.Property(r => r.Description).HasColumnName("description");
        b.Property(r => r.CreatedAt).HasColumnName("created_at");
        b.HasIndex(r => r.RelatedNoteId);
        b.HasIndex(r => r.Project);

        b.HasOne<Note>().WithMany()
            .HasForeignKey(r => r.NoteId)
            .OnDelete(DeleteBehavior.Cascade);
        b.HasOne<Note>().WithMany()
            .HasForeignKey(r => r.RelatedNoteId)
            .OnDelete(DeleteBehavior.Cascade);
        b.HasOne<Project>().WithMany()
            .HasForeignKey(r => r.Project)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class ReflectionConfiguration : IEntityTypeConfiguration<Reflection>
{
    public void Configure(EntityTypeBuilder<Reflection> b)
    {
        b.ToTable("reflections");
        b.HasKey(r => r.Id);
        b.Property(r => r.Id).HasColumnName("id");
        b.Property(r => r.Project).HasColumnName("project_id");
        b.Property(r => r.Scope).HasColumnName("scope").HasMaxLength(200).IsRequired();
        b.Property(r => r.Summary).HasColumnName("summary").IsRequired();
        b.Property(r => r.GeneratedAt).HasColumnName("generated_at");
        b.Property(r => r.GeneratorModel).HasColumnName("generator_model").HasMaxLength(100).IsRequired();
        b.Ignore(r => r.RelatedNotes);
        b.Ignore(r => r.RelatedEntities);
        b.HasIndex(r => r.Project);

        b.HasOne<Project>().WithMany()
            .HasForeignKey(r => r.Project)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class TenantSchemaConfiguration : IEntityTypeConfiguration<TenantSchema>
{
    public void Configure(EntityTypeBuilder<TenantSchema> b)
    {
        b.ToTable("tenant_schemas");
        b.HasKey(t => t.Organization);
        b.Property(t => t.Organization).HasColumnName("organization_id");
        b.Property(t => t.SchemaName).HasColumnName("schema_name").HasMaxLength(63).IsRequired();
        b.Property(t => t.Status).HasColumnName("status").HasConversion<short>();
        b.Property(t => t.CreatedAt).HasColumnName("created_at");
        b.Property(t => t.ActivatedAt).HasColumnName("activated_at");
        b.HasIndex(t => t.SchemaName).IsUnique();

        b.HasOne<Organization>().WithMany()
            .HasForeignKey(t => t.Organization)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class ApiKeyConfiguration : IEntityTypeConfiguration<ApiKey>
{
    public void Configure(EntityTypeBuilder<ApiKey> b)
    {
        b.ToTable("api_keys");
        b.HasKey(k => k.Id);
        b.Property(k => k.Id).HasColumnName("id");
        b.Property(k => k.KeyHash).HasColumnName("key_hash").HasMaxLength(128).IsRequired();
        b.Property(k => k.Organization).HasColumnName("organization_id");
        b.Property(k => k.Project).HasColumnName("project_id");
        b.Property(k => k.CreatedByUser).HasColumnName("created_by_user_id");
        b.Property(k => k.Name).HasColumnName("name").HasMaxLength(200).IsRequired();
        b.Property(k => k.CreatedAt).HasColumnName("created_at");
        b.Property(k => k.LastUsedAt).HasColumnName("last_used_at");
        b.Property(k => k.RevokedAt).HasColumnName("revoked_at");
        b.HasIndex(k => k.KeyHash).IsUnique();
        b.HasIndex(k => k.Project);

        b.HasOne<Organization>().WithMany()
            .HasForeignKey(k => k.Organization)
            .OnDelete(DeleteBehavior.Cascade);
        b.HasOne<Project>().WithMany()
            .HasForeignKey(k => k.Project)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

internal static class JsonbDictionaryConverter
{
    private static readonly JsonSerializerOptions _opts = new(JsonSerializerDefaults.Web);

    public static readonly ValueConverter<Dictionary<string, string>, string> Instance =
        new(
            d => JsonSerializer.Serialize(d, _opts),
            s => JsonSerializer.Deserialize<Dictionary<string, string>>(s, _opts) ?? new Dictionary<string, string>());
}
