using Memory.Domain;
using Memory.Storage.Internal;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Memory.Storage.Configurations;

internal sealed class SkillConfiguration : IEntityTypeConfiguration<Skill>
{
    public void Configure(EntityTypeBuilder<Skill> b)
    {
        b.ToTable("skills");
        b.HasKey(s => s.Id);
        b.Property(s => s.Id).HasColumnName("id");
        b.Property(s => s.Project).HasColumnName("project_id");
        b.Property(s => s.Name).HasColumnName("name").HasMaxLength(SkillMarkdown.MaxNameLength).IsRequired();
        b.Property(s => s.Description).HasColumnName("description").HasMaxLength(SkillMarkdown.MaxDescriptionLength).IsRequired();
        b.Property(s => s.WhenToUse).HasColumnName("when_to_use");
        b.Property(s => s.Body).HasColumnName("body").IsRequired();
        b.Property(s => s.FrontmatterExtraJson).HasColumnName("frontmatter_extra").HasColumnType("jsonb");
        b.Property(s => s.Status).HasColumnName("status").HasConversion<short>();
        b.Property(s => s.Origin).HasColumnName("origin").HasConversion<short>();
        b.Property(s => s.CurrentVersion).HasColumnName("current_version");
        b.Property(s => s.HelpfulCount).HasColumnName("helpful_count");
        b.Property(s => s.HarmfulCount).HasColumnName("harmful_count");
        b.Property(s => s.UsageCount).HasColumnName("usage_count");
        b.Property(s => s.LastUsedAt).HasColumnName("last_used_at");
        b.Property(s => s.CreatedAt).HasColumnName("created_at");
        b.Property(s => s.UpdatedAt).HasColumnName("updated_at");
        b.Property(s => s.DeprecatedAt).HasColumnName("deprecated_at");
        b.Property(s => s.GeneratorModel).HasColumnName("generator_model").HasMaxLength(100);
        b.Property(s => s.UntrustedInput).HasColumnName("untrusted_input").HasDefaultValue(false);
        b.HasIndex(s => new { s.Project, s.Name }).IsUnique();
        b.HasIndex(s => new { s.Project, s.Status });

        b.HasOne<Project>().WithMany()
            .HasForeignKey(s => s.Project)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class SkillVersionConfiguration : IEntityTypeConfiguration<SkillVersion>
{
    public void Configure(EntityTypeBuilder<SkillVersion> b)
    {
        b.ToTable("skill_versions");
        b.HasKey(v => new { v.SkillId, v.Version });
        b.Property(v => v.SkillId).HasColumnName("skill_id");
        b.Property(v => v.Version).HasColumnName("version");
        b.Property(v => v.Project).HasColumnName("project_id");
        b.Property(v => v.Name).HasColumnName("name").HasMaxLength(SkillMarkdown.MaxNameLength).IsRequired();
        b.Property(v => v.Description).HasColumnName("description").HasMaxLength(SkillMarkdown.MaxDescriptionLength).IsRequired();
        b.Property(v => v.WhenToUse).HasColumnName("when_to_use");
        b.Property(v => v.Body).HasColumnName("body").IsRequired();
        b.Property(v => v.FrontmatterExtraJson).HasColumnName("frontmatter_extra").HasColumnType("jsonb");
        b.Property(v => v.ChangeSummary).HasColumnName("change_summary").IsRequired();
        b.Property(v => v.CreatedBy).HasColumnName("created_by").HasMaxLength(200).IsRequired();
        b.Property(v => v.CreatedAt).HasColumnName("created_at");
        b.HasIndex(v => v.Project);

        b.HasOne<Skill>().WithMany()
            .HasForeignKey(v => v.SkillId)
            .OnDelete(DeleteBehavior.Cascade);
        b.HasOne<Project>().WithMany()
            .HasForeignKey(v => v.Project)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class SkillProvenanceConfiguration : IEntityTypeConfiguration<SkillProvenance>
{
    public void Configure(EntityTypeBuilder<SkillProvenance> b)
    {
        b.ToTable("skill_provenance");
        b.HasKey(p => p.Id);
        b.Property(p => p.Id).HasColumnName("id");
        b.Property(p => p.SkillId).HasColumnName("skill_id");
        b.Property(p => p.Version).HasColumnName("version");
        b.Property(p => p.Project).HasColumnName("project_id");
        b.Property(p => p.SourceKind).HasColumnName("source_kind").HasMaxLength(50).IsRequired();
        b.Property(p => p.SourceRef).HasColumnName("source_ref").HasMaxLength(500).IsRequired();
        b.Property(p => p.CreatedAt).HasColumnName("created_at");
        b.HasIndex(p => p.SkillId);
        b.HasIndex(p => p.Project);

        b.HasOne<Skill>().WithMany()
            .HasForeignKey(p => p.SkillId)
            .OnDelete(DeleteBehavior.Cascade);
        b.HasOne<Project>().WithMany()
            .HasForeignKey(p => p.Project)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class SkillEmbeddingConfiguration : IEntityTypeConfiguration<SkillEmbedding>
{
    public void Configure(EntityTypeBuilder<SkillEmbedding> b)
    {
        b.ToTable("skill_embeddings");
        b.HasKey(e => e.SkillId);
        b.Property(e => e.SkillId).HasColumnName("skill_id");
        b.Property(e => e.Project).HasColumnName("project_id");
        b.Property(e => e.EmbeddingModel).HasColumnName("embedding_model").HasMaxLength(100).IsRequired();
        b.Property(e => e.Dimensions).HasColumnName("dimensions");
        b.Property(e => e.Embedding)
            .HasColumnName("embedding")
            .HasColumnType($"vector({NoteEmbeddingConfiguration.DefaultDimensions})")
            .HasConversion(new FloatArrayVectorConverter());
        b.Property(e => e.CreatedAt).HasColumnName("created_at");
        b.HasIndex(e => e.Project);

        b.HasOne<Skill>().WithMany()
            .HasForeignKey(e => e.SkillId)
            .OnDelete(DeleteBehavior.Cascade);
        b.HasOne<Project>().WithMany()
            .HasForeignKey(e => e.Project)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class SkillUsageEventConfiguration : IEntityTypeConfiguration<SkillUsageEvent>
{
    public void Configure(EntityTypeBuilder<SkillUsageEvent> b)
    {
        b.ToTable("skill_usage_events");
        b.HasKey(e => e.Id);
        b.Property(e => e.Id).HasColumnName("id");
        b.Property(e => e.SkillId).HasColumnName("skill_id");
        b.Property(e => e.Project).HasColumnName("project_id");
        b.Property(e => e.Source).HasColumnName("source").HasMaxLength(100).IsRequired();
        b.Property(e => e.SessionRef).HasColumnName("session_ref").HasMaxLength(500);
        b.Property(e => e.Outcome).HasColumnName("outcome").HasConversion<short>();
        b.Property(e => e.Detail).HasColumnName("detail");
        b.Property(e => e.OccurredAt).HasColumnName("occurred_at");
        b.HasIndex(e => e.SkillId);
        b.HasIndex(e => e.Project);

        b.HasOne<Skill>().WithMany()
            .HasForeignKey(e => e.SkillId)
            .OnDelete(DeleteBehavior.Cascade);
        b.HasOne<Project>().WithMany()
            .HasForeignKey(e => e.Project)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class HarvestedSessionConfiguration : IEntityTypeConfiguration<HarvestedSession>
{
    public void Configure(EntityTypeBuilder<HarvestedSession> b)
    {
        b.ToTable("harvested_sessions");
        b.HasKey(h => h.Id);
        b.Property(h => h.Id).HasColumnName("id");
        b.Property(h => h.Project).HasColumnName("project_id");
        b.Property(h => h.Source).HasColumnName("source").HasMaxLength(100).IsRequired();
        b.Property(h => h.SessionId).HasColumnName("session_id").HasMaxLength(200).IsRequired();
        b.Property(h => h.ContentHash).HasColumnName("content_hash").HasMaxLength(128).IsRequired();
        b.Property(h => h.TranscriptGzip).HasColumnName("transcript_gzip");
        b.Property(h => h.TranscriptPath).HasColumnName("transcript_path").HasMaxLength(1000);
        b.Property(h => h.Status).HasColumnName("status").HasConversion<short>();
        b.Property(h => h.StatsJson).HasColumnName("stats").HasColumnType("jsonb");
        b.Property(h => h.SubmittedAt).HasColumnName("submitted_at");
        b.Property(h => h.ProcessedAt).HasColumnName("processed_at");
        b.Property(h => h.Error).HasColumnName("error");
        b.HasIndex(h => new { h.Project, h.ContentHash }).IsUnique();
        b.HasIndex(h => new { h.Project, h.Status });

        b.HasOne<Project>().WithMany()
            .HasForeignKey(h => h.Project)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
