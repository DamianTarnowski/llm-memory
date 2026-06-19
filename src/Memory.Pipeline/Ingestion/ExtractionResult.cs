using System.Text.Json;
using System.Text.Json.Serialization;

namespace Memory.Pipeline.Ingestion;

public sealed record ExtractedNote(
    string Content,
    string ContextDescription,
    List<string> Keywords,
    List<string> Tags,
    Memory.Domain.NoteKind Kind = Memory.Domain.NoteKind.General,
    Memory.Domain.MemoryType MemoryType = Memory.Domain.MemoryType.Semantic);

public sealed record ExtractedEntity(
    string Name,
    string Kind,
    [property: JsonConverter(typeof(LenientStringDictionaryConverter))]
    Dictionary<string, string> Attributes);

public sealed record ExtractedRelationship(
    string From,
    string To,
    string Relation,
    [property: JsonConverter(typeof(LenientStringDictionaryConverter))]
    Dictionary<string, string> Properties);

internal sealed class LenientStringDictionaryConverter : JsonConverter<Dictionary<string, string>>
{
    public override Dictionary<string, string> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var result = new Dictionary<string, string>();
        if (reader.TokenType == JsonTokenType.Null) return result;
        if (reader.TokenType != JsonTokenType.StartObject) return result;

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject) return result;
            if (reader.TokenType != JsonTokenType.PropertyName) continue;
            var key = reader.GetString() ?? "";
            reader.Read();
            result[key] = reader.TokenType switch
            {
                JsonTokenType.String => reader.GetString() ?? "",
                JsonTokenType.Number => reader.TryGetInt64(out var i) ? i.ToString() : reader.GetDouble().ToString("G"),
                JsonTokenType.True => "true",
                JsonTokenType.False => "false",
                JsonTokenType.Null => "",
                JsonTokenType.StartArray or JsonTokenType.StartObject =>
                    JsonSerializer.Serialize(JsonElement.ParseValue(ref reader)),
                _ => "",
            };
        }
        return result;
    }

    public override void Write(Utf8JsonWriter writer, Dictionary<string, string> value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        foreach (var kv in value)
        {
            writer.WriteString(kv.Key, kv.Value);
        }
        writer.WriteEndObject();
    }
}

public sealed record ExtractionResult(
    List<ExtractedNote> Notes,
    List<ExtractedEntity> Entities,
    List<ExtractedRelationship> Relationships,
    List<RelationshipTriple>? SupersedesPriorEdges = null);

public sealed record RelationshipTriple(string From, string To, string Relation);
