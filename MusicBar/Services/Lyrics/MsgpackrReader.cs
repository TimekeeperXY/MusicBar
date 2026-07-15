using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace MusicBar.Services.Lyrics;

internal sealed class MsgpackrReader
{
    private const byte RecordExtensionType = 0x72;
    private readonly ReadOnlyMemory<byte> _source;
    private readonly Dictionary<byte, string[]> _structures = [];
    private int _position;
    private string? _bundledString0;
    private string? _bundledString1;
    private int _bundlePosition0;
    private int _bundlePosition1;
    private int? _postBundlePosition;

    public MsgpackrReader(ReadOnlyMemory<byte> source) => _source = source;

    public object? Read()
    {
        var result = ReadValue(0);
        if (_postBundlePosition is int postBundlePosition)
        {
            _position = postBundlePosition;
        }
        return result;
    }

    private object? ReadValue(int depth)
    {
        if (depth > 128)
        {
            throw new InvalidDataException("Msgpackr nesting limit exceeded.");
        }

        var marker = ReadByte();
        if (marker < 0x40) return (long)marker;
        if (marker <= 0x7f)
        {
            return _structures.TryGetValue((byte)(marker & 0x3f), out var structure)
                ? ReadRecordValues(structure, depth + 1)
                : (long)marker;
        }
        if (marker >= 0xe0) return (long)(sbyte)marker;
        if ((marker & 0xe0) == 0xa0) return ReadString(marker & 0x1f);
        if ((marker & 0xf0) == 0x90) return ReadArray(marker & 0x0f, depth);
        if ((marker & 0xf0) == 0x80) return ReadMap(marker & 0x0f, depth);

        return marker switch
        {
            0xc0 => null,
            0xc1 => ReadBundledString(depth),
            0xc2 => false,
            0xc3 => true,
            0xc4 => ReadBinary(ReadByte()),
            0xc5 => ReadBinary(ReadUInt16()),
            0xc6 => ReadBinary(checked((int)ReadUInt32())),
            0xc7 => ReadExtension(ReadByte(), depth),
            0xc8 => ReadExtension(ReadUInt16(), depth),
            0xc9 => ReadExtension(checked((int)ReadUInt32()), depth),
            0xca => ReadSingle(),
            0xcb => ReadDouble(),
            0xcc => (long)ReadByte(),
            0xcd => (long)ReadUInt16(),
            0xce => (long)ReadUInt32(),
            0xcf => ReadUInt64(),
            0xd0 => (long)(sbyte)ReadByte(),
            0xd1 => (long)ReadInt16(),
            0xd2 => (long)ReadInt32(),
            0xd3 => ReadInt64(),
            0xd4 => ReadExtension(1, depth),
            0xd5 => ReadExtension(2, depth),
            0xd6 => ReadExtension(4, depth),
            0xd7 => ReadExtension(8, depth),
            0xd8 => ReadExtension(16, depth),
            0xd9 => ReadString(ReadByte()),
            0xda => ReadString(ReadUInt16()),
            0xdb => ReadString(checked((int)ReadUInt32())),
            0xdc => ReadArray(ReadUInt16(), depth),
            0xdd => ReadArray(checked((int)ReadUInt32()), depth),
            0xde => ReadMap(ReadUInt16(), depth),
            0xdf => ReadMap(checked((int)ReadUInt32()), depth),
            _ => throw new InvalidDataException($"Unsupported Msgpack marker 0x{marker:X2}.")
        };
    }

    private object? ReadExtension(int length, int depth)
    {
        var type = ReadByte();
        if (type == RecordExtensionType && length == 1)
        {
            return ReadRecord(ReadByte(), depth + 1);
        }

        if (type == 0x62 && length == 4)
        {
            return ReadBundledStrings(depth + 1);
        }

        // msgpackr encodes these JavaScript types as an extension immediately
        // followed by the wrapped MessagePack value. The lyric cache only needs
        // that value; preserving the JS-specific wrapper is unnecessary.
        if (type is 0x65 or 0x69 or 0x73 or 0x78)
        {
            Skip(length);
            return ReadValue(depth + 1);
        }

        Skip(length);
        return null;
    }

    private object? ReadBundledStrings(int depth)
    {
        var dataSize = checked((int)ReadUInt32());
        var dataPosition = _position;
        var stringsPosition = checked(_position + dataSize - 4);
        if (stringsPosition < dataPosition || stringsPosition >= _source.Length)
        {
            throw new InvalidDataException("Invalid Msgpackr bundled string block.");
        }

        _position = stringsPosition;
        _bundledString0 = ReadOnlyString();
        _bundledString1 = ReadOnlyString();
        _bundlePosition0 = 0;
        _bundlePosition1 = 0;
        _postBundlePosition = _position;
        _position = dataPosition;
        return ReadValue(depth + 1);
    }

    private string ReadBundledString(int depth)
    {
        if (_bundledString0 is null || _bundledString1 is null)
        {
            throw new InvalidDataException("Msgpackr bundled string reference has no active bundle.");
        }

        var lengthValue = ReadValue(depth + 1);
        var length = lengthValue switch
        {
            long value => checked((int)value),
            ulong value => checked((int)value),
            _ => throw new InvalidDataException("Invalid Msgpackr bundled string length.")
        };

        if (length > 0)
        {
            var result = SliceBundle(_bundledString1, _bundlePosition1, length);
            _bundlePosition1 += length;
            return result;
        }

        var absoluteLength = checked(-length);
        var negativeResult = SliceBundle(_bundledString0, _bundlePosition0, absoluteLength);
        _bundlePosition0 += absoluteLength;
        return negativeResult;
    }

    private static string SliceBundle(string bundle, int position, int length)
    {
        if (position < 0 || length < 0 || position > bundle.Length - length)
        {
            throw new InvalidDataException("Msgpackr bundled string reference is out of range.");
        }
        return bundle.Substring(position, length);
    }

    private string ReadOnlyString()
    {
        var marker = ReadByte();
        var length = marker switch
        {
            >= 0xa0 and < 0xc0 => marker - 0xa0,
            0xd9 => ReadByte(),
            0xda => ReadUInt16(),
            0xdb => checked((int)ReadUInt32()),
            _ => throw new InvalidDataException("Expected Msgpack string.")
        };
        return ReadString(length);
    }

    private Dictionary<string, object?> ReadRecord(byte id, int depth)
    {
        var structureId = (byte)(id & 0x3f);
        if (!_structures.TryGetValue(structureId, out var fields))
        {
            var definition = ReadValue(depth + 1) as List<object?>
                ?? throw new InvalidDataException("Msgpackr record definition is missing.");
            fields = definition.Select(value => value as string
                    ?? throw new InvalidDataException("Invalid Msgpackr record field."))
                .ToArray();
            _structures[structureId] = fields;
        }

        return ReadRecordValues(fields, depth + 1);
    }

    private Dictionary<string, object?> ReadRecordValues(string[] fields, int depth)
    {
        var result = new Dictionary<string, object?>(fields.Length, StringComparer.Ordinal);
        foreach (var field in fields)
        {
            result[field] = ReadValue(depth + 1);
        }
        return result;
    }

    private List<object?> ReadArray(int count, int depth)
    {
        if (count < 0 || count > 1_000_000)
        {
            throw new InvalidDataException("Invalid Msgpack array length.");
        }

        var result = new List<object?>(count);
        for (var index = 0; index < count; index++)
        {
            result.Add(ReadValue(depth + 1));
        }
        return result;
    }

    private Dictionary<string, object?> ReadMap(int count, int depth)
    {
        if (count < 0 || count > 1_000_000)
        {
            throw new InvalidDataException("Invalid Msgpack map length.");
        }

        var result = new Dictionary<string, object?>(count, StringComparer.Ordinal);
        for (var index = 0; index < count; index++)
        {
            var key = ReadValue(depth + 1)?.ToString()
                ?? throw new InvalidDataException("Msgpack map key is null.");
            result[key] = ReadValue(depth + 1);
        }
        return result;
    }

    private string ReadString(int length)
    {
        var span = ReadSpan(length);
        return Encoding.UTF8.GetString(span);
    }

    private byte[] ReadBinary(int length) => ReadSpan(length).ToArray();

    private float ReadSingle()
    {
        var bits = BinaryPrimitives.ReadInt32BigEndian(ReadSpan(4));
        return BitConverter.Int32BitsToSingle(bits);
    }

    private double ReadDouble()
    {
        var bits = BinaryPrimitives.ReadInt64BigEndian(ReadSpan(8));
        return BitConverter.Int64BitsToDouble(bits);
    }

    private short ReadInt16() => BinaryPrimitives.ReadInt16BigEndian(ReadSpan(2));
    private int ReadInt32() => BinaryPrimitives.ReadInt32BigEndian(ReadSpan(4));
    private long ReadInt64() => BinaryPrimitives.ReadInt64BigEndian(ReadSpan(8));
    private ushort ReadUInt16() => BinaryPrimitives.ReadUInt16BigEndian(ReadSpan(2));
    private uint ReadUInt32() => BinaryPrimitives.ReadUInt32BigEndian(ReadSpan(4));
    private ulong ReadUInt64() => BinaryPrimitives.ReadUInt64BigEndian(ReadSpan(8));

    private byte ReadByte()
    {
        if (_position >= _source.Length)
        {
            throw new EndOfStreamException();
        }
        return _source.Span[_position++];
    }

    private ReadOnlySpan<byte> ReadSpan(int length)
    {
        if (length < 0 || _position > _source.Length - length)
        {
            throw new EndOfStreamException();
        }
        var result = _source.Span.Slice(_position, length);
        _position += length;
        return result;
    }

    private void Skip(int length) => _ = ReadSpan(length);
}
