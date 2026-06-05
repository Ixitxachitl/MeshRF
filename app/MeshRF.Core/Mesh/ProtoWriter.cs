// SPDX-License-Identifier: GPL-3.0-or-later
namespace MeshRF.Mesh;

/// <summary>
/// Minimal protobuf wire-format writer — the counterpart to
/// <see cref="ProtoReader"/>. Implements just the subset needed to build the
/// Meshtastic messages this app transmits (<c>Data</c>): varints, 32-bit fixed
/// fields and length-delimited (string/bytes/sub-message) fields. Kept
/// hand-rolled to match the reader and avoid a Google.Protobuf dependency.
/// </summary>
public sealed class ProtoWriter
{
    private readonly List<byte> _buf = new();

    /// <summary>Number of bytes written so far.</summary>
    public int Length => _buf.Count;

    /// <summary>Snapshot of the written bytes.</summary>
    public byte[] ToArray() => _buf.ToArray();

    private void WriteTag(int fieldNumber, ProtoReader.WireType wireType)
        => WriteVarint((ulong)((fieldNumber << 3) | (int)wireType));

    /// <summary>Write a raw varint (no tag).</summary>
    public void WriteVarint(ulong value)
    {
        do
        {
            byte b = (byte)(value & 0x7F);
            value >>= 7;
            if (value != 0) b |= 0x80;
            _buf.Add(b);
        } while (value != 0);
    }

    /// <summary>Write a varint field (wire type 0).</summary>
    public void WriteVarintField(int fieldNumber, ulong value)
    {
        WriteTag(fieldNumber, ProtoReader.WireType.Varint);
        WriteVarint(value);
    }

    /// <summary>Write a 32-bit fixed field (wire type 5, little-endian).</summary>
    public void WriteFixed32Field(int fieldNumber, uint value)
    {
        WriteTag(fieldNumber, ProtoReader.WireType.I32);
        _buf.Add((byte)(value & 0xFF));
        _buf.Add((byte)((value >> 8) & 0xFF));
        _buf.Add((byte)((value >> 16) & 0xFF));
        _buf.Add((byte)((value >> 24) & 0xFF));
    }

    /// <summary>Write a 32-bit IEEE-754 float field (wire type 5).</summary>
    public void WriteFloatField(int fieldNumber, float value)
        => WriteFixed32Field(fieldNumber, (uint)BitConverter.SingleToInt32Bits(value));

    /// <summary>Write a length-delimited field (wire type 2): bytes/string/sub-msg.</summary>
    public void WriteBytesField(int fieldNumber, ReadOnlySpan<byte> value)
    {
        WriteTag(fieldNumber, ProtoReader.WireType.Len);
        WriteVarint((ulong)value.Length);
        foreach (var b in value) _buf.Add(b);
    }

    /// <summary>Write a UTF-8 string field (wire type 2).</summary>
    public void WriteStringField(int fieldNumber, string value)
        => WriteBytesField(fieldNumber, System.Text.Encoding.UTF8.GetBytes(value));
}
