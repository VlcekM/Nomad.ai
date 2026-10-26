using System.Text.Json;
using System.Text.Json.Serialization;

namespace Klodi.Core.Protocol;

/// <summary>
/// Message content is either a plain string (user turns in transcripts) or an array of blocks; this
/// normalises both to a list of blocks. Uses the source-generated metadata, so it is AOT-safe.
/// </summary>
public sealed class ContentBlockListConverter : JsonConverter<List<WireContentBlock>>
{
    public override List<WireContentBlock>? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Null:
                return null;
            case JsonTokenType.String:
                return [new WireContentBlock { Type = "text", Text = reader.GetString() }];
            case JsonTokenType.StartArray:
                var list = new List<WireContentBlock>();
                while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                {
                    if (reader.TokenType == JsonTokenType.StartObject)
                    {
                        var block = JsonSerializer.Deserialize(ref reader, ProtocolJsonContext.Default.WireContentBlock);
                        if (block is not null)
                        {
                            list.Add(block);
                        }
                    }
                    else
                    {
                        reader.Skip();
                    }
                }

                return list;
            default:
                reader.Skip();
                return null;
        }
    }

    public override void Write(Utf8JsonWriter writer, List<WireContentBlock> value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        foreach (var block in value)
        {
            JsonSerializer.Serialize(writer, block, ProtocolJsonContext.Default.WireContentBlock);
        }

        writer.WriteEndArray();
    }
}
