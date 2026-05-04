using Memory.Domain;

namespace Memory.Pipeline.Linking;

public interface INoteLinker
{
    Task<int> LinkRecentNoteAsync(NoteId noteId, float[] noteEmbedding, CancellationToken ct = default);
}
