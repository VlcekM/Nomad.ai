using System.Text.Json;

namespace Nomad.Core.Usage;

/// <summary>Projects the small set of usage fields from a JSONL stream without buffering its transcript text.</summary>
internal sealed class TokenLogReader(Stream stream, long length, bool codex)
{
    private enum Shape { Root, Message, Payload, Info, Usage }

    private const int MaxFieldBytes = 4096;
    private readonly byte[] _buffer = new byte[32 * 1024];
    private readonly byte[] _field = new byte[MaxFieldBytes];
    private readonly byte[] _projection = new byte[16 * 1024];
    private int _next;
    private int _end;

    public long Position { get; private set; } = stream.Position;
    public bool EndedWithNewline { get; private set; }

    public JsonDocument? ReadLine()
    {
        var available = Available();
        if (Position == 0 && available.StartsWith("\uFEFF"u8))
        {
            Advance(3);
            available = Available();
        }
        var newline = available.IndexOf((byte)'\n');
        if (newline >= 0 || available.Length == length - Position)
        {
            var size = newline >= 0 ? newline : available.Length;
            var line = available[..size];
            var relevant = codex ? line.IndexOf("token_count"u8) >= 0
                : line.IndexOf("input_tokens"u8) >= 0 && line.IndexOf("usage"u8) >= 0;
            var memory = _buffer.AsMemory(_next, size);
            EndedWithNewline = newline >= 0;
            Advance(size + (EndedWithNewline ? 1 : 0));
            if (!relevant) return null;
            try { return JsonDocument.Parse(memory); }
            catch (JsonException) { return null; }
        }

        using var projected = new MemoryStream(_projection, 0, _projection.Length, writable: true, publiclyVisible: true);
        projected.SetLength(0);
        JsonDocument? row = null;
        try
        {
            using var writer = new Utf8JsonWriter(projected);
            ReadObject(writer, Shape.Root);
            Space();
            if (Peek() is -1 or '\n')
            {
                writer.Flush();
                row = JsonDocument.Parse(_projection.AsMemory(0, (int)projected.Length));
            }
        }
        catch (Exception e) when (e is JsonException or NotSupportedException) { }

        while (!Available().IsEmpty)
        {
            var end = Available().IndexOf((byte)'\n');
            Advance(end >= 0 ? end : Available().Length);
            if (end >= 0) break;
        }
        EndedWithNewline = Peek() == '\n';
        if (EndedWithNewline) Read();
        return row;
    }

    private void ReadObject(Utf8JsonWriter writer, Shape shape)
    {
        Space();
        Expect('{');
        writer.WriteStartObject();
        Space();
        if (Peek() != '}')
        {
            while (true)
            {
                Space();
                var key = StringValue();
                Space();
                Expect(':');
                Space();

                switch (shape, key)
                {
                    case (Shape.Root, "message"): ObjectOrSkip(writer, key, Shape.Message); break;
                    case (Shape.Root, "payload"): ObjectOrSkip(writer, key, Shape.Payload); break;
                    case (Shape.Payload, "info"): ObjectOrSkip(writer, key, Shape.Info); break;
                    case (Shape.Message, "usage"):
                    case (Shape.Info, "total_token_usage"): ObjectOrSkip(writer, key, Shape.Usage); break;
                    case (Shape.Root, "type" or "timestamp"):
                    case (Shape.Message, "id"):
                    case (Shape.Payload, "type"):
                        if (Peek() == '"')
                        {
                            if (StringValue() is { } text) writer.WriteString(key, text);
                        }
                        else SkipValue();
                        break;
                    case (Shape.Usage, "input_tokens" or "output_tokens" or "cache_read_input_tokens" or "cache_creation_input_tokens"):
                        if (Peek() is not ('-' or >= '0' and <= '9'))
                        {
                            SkipValue();
                            break;
                        }
                        var count = 0;
                        while (Peek() is >= 0 and not ('\n' or ',' or '}' or ']' or ' ' or '\t' or '\r'))
                        {
                            var value = (byte)Read();
                            if (count < _field.Length) _field[count++] = value;
                        }
                        if (System.Buffers.Text.Utf8Parser.TryParse(_field.AsSpan(0, count), out long number, out var used) && used == count)
                        {
                            writer.WriteNumber(key, number);
                        }
                        break;
                    default: SkipValue(); break;
                }

                Space();
                if (Peek() != ',') break;
                Read();
            }
        }
        Expect('}');
        writer.WriteEndObject();
    }

    private void ObjectOrSkip(Utf8JsonWriter writer, string key, Shape shape)
    {
        if (Peek() != '{')
        {
            SkipValue();
            return;
        }
        writer.WritePropertyName(key);
        ReadObject(writer, shape);
    }

    private string? StringValue()
    {
        var size = 0;
        _field[size++] = (byte)'"';
        if (!ReadString(capture: true, ref size)) return null;
        return JsonSerializer.Deserialize(_field.AsSpan(0, size), TokenLogJson.Default.String);
    }

    private bool ReadString(bool capture, ref int size)
    {
        Expect('"');
        while (true)
        {
            var available = Available();
            if (available.IsEmpty) throw new JsonException();
            var special = available.IndexOfAny((byte)'"', (byte)'\\', (byte)'\n');
            if (special < 0) special = available.Length;
            var part = available[..special];
            if (capture && part.Length <= _field.Length - size)
            {
                part.CopyTo(_field.AsSpan(size));
                size += part.Length;
            }
            else capture = false;
            Advance(special);
            if (special == available.Length) continue;
            if (Peek() == '\n') throw new JsonException();

            var marker = Read();
            if (capture && size < _field.Length) _field[size++] = (byte)marker;
            else capture = false;
            if (marker == '"') return capture;

            if (Peek() is -1 or '\n') throw new JsonException();
            var escaped = Read();
            if (capture && size < _field.Length) _field[size++] = (byte)escaped;
            else capture = false;
        }
    }

    private void SkipValue()
    {
        var nesting = 0;
        do
        {
            switch (Peek())
            {
                case '"':
                    var unused = 0;
                    ReadString(capture: false, ref unused);
                    break;
                case '{' or '[':
                    nesting++;
                    Read();
                    break;
                case '}' or ']':
                    if (nesting == 0) return;
                    nesting--;
                    Read();
                    break;
                case -1 or '\n': throw new JsonException();
                case ',' or ' ' or '\t' or '\r' when nesting == 0: return;
                default: Read(); break;
            }
        } while (nesting > 0 || Peek() is not (-1 or '\n' or ',' or '}' or ']' or ' ' or '\t' or '\r'));
    }

    private void Space()
    {
        while (Peek() is ' ' or '\t' or '\r') Read();
    }

    private void Expect(char value)
    {
        if (Peek() != value) throw new JsonException();
        Read();
    }

    private int Peek() => Available().IsEmpty ? -1 : _buffer[_next];

    private int Read()
    {
        var value = Peek();
        if (value >= 0) Advance(1);
        return value;
    }

    private ReadOnlySpan<byte> Available()
    {
        if (_next == _end && Position < length)
        {
            _next = 0;
            _end = stream.Read(_buffer.AsSpan(0, (int)Math.Min(_buffer.Length, length - Position)));
        }
        return _buffer.AsSpan(_next, _end - _next);
    }

    private void Advance(int count)
    {
        _next += count;
        Position += count;
    }
}

[System.Text.Json.Serialization.JsonSerializable(typeof(string))]
internal sealed partial class TokenLogJson : System.Text.Json.Serialization.JsonSerializerContext;
