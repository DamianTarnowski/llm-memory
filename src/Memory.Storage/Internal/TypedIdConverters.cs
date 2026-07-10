using Memory.Domain;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Memory.Storage.Internal;

internal sealed class OrganizationIdConverter()
    : ValueConverter<OrganizationId, Guid>(id => id.Value, value => new OrganizationId(value));

internal sealed class UserIdConverter()
    : ValueConverter<UserId, Guid>(id => id.Value, value => new UserId(value));

internal sealed class ProjectIdConverter()
    : ValueConverter<ProjectId, Guid>(id => id.Value, value => new ProjectId(value));

internal sealed class EpisodeIdConverter()
    : ValueConverter<EpisodeId, Guid>(id => id.Value, value => new EpisodeId(value));

internal sealed class NoteIdConverter()
    : ValueConverter<NoteId, Guid>(id => id.Value, value => new NoteId(value));

internal sealed class EntityIdConverter()
    : ValueConverter<EntityId, Guid>(id => id.Value, value => new EntityId(value));

internal sealed class EdgeIdConverter()
    : ValueConverter<EdgeId, Guid>(id => id.Value, value => new EdgeId(value));

internal sealed class ReflectionIdConverter()
    : ValueConverter<ReflectionId, Guid>(id => id.Value, value => new ReflectionId(value));

internal sealed class SkillIdConverter()
    : ValueConverter<SkillId, Guid>(id => id.Value, value => new SkillId(value));
