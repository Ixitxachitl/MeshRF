// SPDX-License-Identifier: GPL-3.0-or-later
namespace MeshtasticRF.Mesh;

/// <summary>
/// Minimal protobuf wire-format reader. Only the subset of features needed to
/// decode the handful of Meshtastic messages this app understands
/// (<c>Data</c>, <c>User</c>, <c>Position</c>) is implemented — varints,
/// 32/64-bit fixed fields and length-delimited (string/bytes/sub-message)
/// fields. We deliberately hand-roll this instead of pulling in
/// Google.Protobuf + the full .proto set to keep the dependency surface small.
/// </summary>
public ref struct ProtoReader
{
    private ReadOnlySpan<byte> _data;
    private int _pos;

    public ProtoReader(ReadOnlySpan<byte> data)
    {
        _data = data;
        _pos = 0;
    }

    public readonly bool End => _pos >= _data.Length;

    /// <summary>Wire types per the protobuf spec.</summary>
    public enum WireType
    {
        Varint = 0,
        I64 = 1,
        Len = 2,
        I32 = 5,
    }

    /// <summary>
    /// Read the next field tag. Returns false at end of buffer. On success,
    /// <paramref name="fieldNumber"/> and <paramref name="wireType"/> describe
    /// the field that follows.
    /// </summary>
    public bool TryReadTag(out int fieldNumber, out WireType wireType)
    {
        fieldNumber = 0;
        wireType = WireType.Varint;
        if (End) return false;
        ulong tag = ReadVarint();
        fieldNumber = (int)(tag >> 3);
        wireType = (WireType)(tag & 0x7);
        return fieldNumber > 0;
    }

    public ulong ReadVarint()
    {
        ulong result = 0;
        int shift = 0;
        while (_pos < _data.Length && shift < 64)
        {
            byte b = _data[_pos++];
            result |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) break;
            shift += 7;
        }
        return result;
    }

    public uint ReadFixed32()
    {
        if (_pos + 4 > _data.Length) { _pos = _data.Length; return 0; }
        uint v = (uint)(_data[_pos]
                        | _data[_pos + 1] << 8
                        | _data[_pos + 2] << 16
                        | _data[_pos + 3] << 24);
        _pos += 4;
        return v;
    }

    public ulong ReadFixed64()
    {
        ulong lo = ReadFixed32();
        ulong hi = ReadFixed32();
        return lo | (hi << 32);
    }

    /// <summary>Read a 32-bit IEEE-754 float (proto wire type I32).</summary>
    public float ReadFloat() => BitConverter.Int32BitsToSingle((int)ReadFixed32());

    /// <summary>Read a length-delimited field as a raw span (no copy).</summary>
    public ReadOnlySpan<byte> ReadLengthDelimited()
    {
        int len = (int)ReadVarint();
        if (len < 0 || _pos + len > _data.Length) { _pos = _data.Length; return default; }
        var span = _data.Slice(_pos, len);
        _pos += len;
        return span;
    }

    public string ReadString()
    {
        var span = ReadLengthDelimited();
        return span.IsEmpty ? string.Empty
            : System.Text.Encoding.UTF8.GetString(span);
    }

    /// <summary>Skip the value of a field we don't care about.</summary>
    public void SkipField(WireType wireType)
    {
        switch (wireType)
        {
            case WireType.Varint: ReadVarint(); break;
            case WireType.I64:    ReadFixed64(); break;
            case WireType.I32:    ReadFixed32(); break;
            case WireType.Len:    ReadLengthDelimited(); break;
            default:              _pos = _data.Length; break;
        }
    }
}
