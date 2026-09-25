using System.Buffers.Binary;

namespace RosAtlasBridge.Ros;

/// <summary>
/// Reads CDR the way ROS 2 serializes it: a 4-byte encapsulation header, then fields with every
/// primitive aligned to its own size, measured from the first byte after the header.
/// </summary>
public sealed class CdrReader
{
    private readonly byte[] buffer;
    private readonly int origin;
    private readonly int end;
    private readonly bool littleEndian;
    private readonly int maxAlignment;
    private int position;

    public CdrReader(byte[] buffer, int offset, int count)
    {
        if (count < 4)
        {
            throw new FormatException("CDR payload is shorter than its 4-byte header");
        }

        // Header byte 1 is the representation: 0x00/0x01 = classic CDR (XCDR1, the ROS 2 default)
        // big/little endian; 0x06/0x07 = plain XCDR2, which caps alignment at 4 bytes.
        var representation = buffer[offset + 1];
        if (buffer[offset] != 0 || representation is not (0x00 or 0x01 or 0x06 or 0x07))
        {
            throw new NotSupportedException($"unsupported CDR representation 0x{buffer[offset]:X2}{representation:X2}");
        }

        this.buffer = buffer;
        this.littleEndian = (representation & 1) == 1;
        this.maxAlignment = representation >= 0x06 ? 4 : 8;
        this.origin = offset + 4;
        this.position = this.origin;
        this.end = offset + count;
    }

    public static int SizeOf(PrimitiveType type) => type switch
    {
        PrimitiveType.Bool or PrimitiveType.Byte or PrimitiveType.Char or PrimitiveType.Int8 or PrimitiveType.UInt8 => 1,
        PrimitiveType.Int16 or PrimitiveType.UInt16 => 2,
        PrimitiveType.Int32 or PrimitiveType.UInt32 or PrimitiveType.Float32 => 4,
        PrimitiveType.Int64 or PrimitiveType.UInt64 or PrimitiveType.Float64 => 8,
        _ => throw new ArgumentException($"{type} has no fixed size"),
    };

    /// <summary>Reads any numeric primitive (bool as 0/1) as a double.</summary>
    public double ReadPrimitive(PrimitiveType type) => type switch
    {
        PrimitiveType.Bool => this.ReadByte() != 0 ? 1.0 : 0.0,
        PrimitiveType.Byte or PrimitiveType.Char or PrimitiveType.UInt8 => this.ReadByte(),
        PrimitiveType.Int8 => (sbyte)this.ReadByte(),
        PrimitiveType.Int16 => this.ReadInt16(),
        PrimitiveType.UInt16 => this.ReadUInt16(),
        PrimitiveType.Int32 => this.ReadInt32(),
        PrimitiveType.UInt32 => this.ReadUInt32(),
        PrimitiveType.Int64 => this.ReadInt64(),
        PrimitiveType.UInt64 => this.ReadUInt64(),
        PrimitiveType.Float32 => this.ReadFloat32(),
        PrimitiveType.Float64 => this.ReadFloat64(),
        _ => throw new ArgumentException($"{type} is not numeric"),
    };

    public byte ReadByte() => this.Take(1)[0];

    public short ReadInt16()
    {
        var s = this.TakeAligned(2);
        return this.littleEndian ? BinaryPrimitives.ReadInt16LittleEndian(s) : BinaryPrimitives.ReadInt16BigEndian(s);
    }

    public ushort ReadUInt16()
    {
        var s = this.TakeAligned(2);
        return this.littleEndian ? BinaryPrimitives.ReadUInt16LittleEndian(s) : BinaryPrimitives.ReadUInt16BigEndian(s);
    }

    public int ReadInt32()
    {
        var s = this.TakeAligned(4);
        return this.littleEndian ? BinaryPrimitives.ReadInt32LittleEndian(s) : BinaryPrimitives.ReadInt32BigEndian(s);
    }

    public uint ReadUInt32()
    {
        var s = this.TakeAligned(4);
        return this.littleEndian ? BinaryPrimitives.ReadUInt32LittleEndian(s) : BinaryPrimitives.ReadUInt32BigEndian(s);
    }

    public long ReadInt64()
    {
        var s = this.TakeAligned(8);
        return this.littleEndian ? BinaryPrimitives.ReadInt64LittleEndian(s) : BinaryPrimitives.ReadInt64BigEndian(s);
    }

    public ulong ReadUInt64()
    {
        var s = this.TakeAligned(8);
        return this.littleEndian ? BinaryPrimitives.ReadUInt64LittleEndian(s) : BinaryPrimitives.ReadUInt64BigEndian(s);
    }

    public float ReadFloat32()
    {
        var s = this.TakeAligned(4);
        return this.littleEndian ? BinaryPrimitives.ReadSingleLittleEndian(s) : BinaryPrimitives.ReadSingleBigEndian(s);
    }

    public double ReadFloat64()
    {
        var s = this.TakeAligned(8);
        return this.littleEndian ? BinaryPrimitives.ReadDoubleLittleEndian(s) : BinaryPrimitives.ReadDoubleBigEndian(s);
    }

    public void SkipString() => this.Take(checked((int)this.ReadUInt32()));

    /// <summary>Skips a wstring, assuming Fast DDS's encoding: uint32 length, then 4-byte characters.</summary>
    public void SkipWString() => this.Skip(checked((int)this.ReadUInt32()), 4);

    /// <summary>Skips consecutive numeric primitives without decoding them (oversized arrays).</summary>
    public void SkipPrimitives(PrimitiveType type, int count)
    {
        if (count > 0)
        {
            this.Skip(count, SizeOf(type));
        }
    }

    private void Skip(int count, int elementSize)
    {
        this.Align(elementSize);
        this.Take(checked(count * elementSize));
    }

    private ReadOnlySpan<byte> TakeAligned(int size)
    {
        this.Align(size);
        return this.Take(size);
    }

    private void Align(int size)
    {
        var alignment = Math.Min(size, this.maxAlignment);
        var misalignment = (this.position - this.origin) % alignment;
        if (misalignment != 0)
        {
            this.position += alignment - misalignment;
        }
    }

    private ReadOnlySpan<byte> Take(int count)
    {
        if (count < 0 || this.position + count > this.end)
        {
            throw new FormatException($"CDR payload ended early (needed {count} bytes at offset {this.position - this.origin})");
        }

        var span = new ReadOnlySpan<byte>(this.buffer, this.position, count);
        this.position += count;
        return span;
    }
}
