// SPDX-License-Identifier: GPL-3.0-or-later
namespace MeshRF.Mesh;

/// <summary>
/// Meshtastic application port numbers, mirroring the schema's PortNum minus
/// its MAX sentinel. Completeness matters: MeshDecoder treats a portnum absent
/// from this enum as a failed decrypt, so a value missing here silently
/// discards every packet on that port.
/// </summary>
public enum PortNum
{
    Unknown            = 0,
    TextMessage        = 1,
    RemoteHardware     = 2,
    Position           = 3,
    NodeInfo           = 4,
    Routing            = 5,
    Admin              = 6,
    TextMessageCompressed = 7,
    Waypoint           = 8,
    Audio              = 9,
    DetectionSensor    = 10,
    Alert              = 11,
    KeyVerification    = 12,
    RemoteShell        = 13,
    Reply              = 32,
    IpTunnel           = 33,
    PaxCounter         = 34,
    StoreForwardPlusPlus = 35,
    NodeStatus         = 36,
    MeshBeacon         = 37,
    Serial             = 64,
    StoreForward       = 65,
    RangeTest          = 66,
    Telemetry          = 67,
    Zps                = 68,
    Simulator          = 69,
    Traceroute         = 70,
    NeighborInfo       = 71,
    AtakPlugin         = 72,
    MapReport          = 73,
    PowerStress        = 74,
    LorawanBridge      = 75,
    ReticulumTunnel    = 76,
    Cayenne            = 77,
    AtakPluginV2       = 78,
    LoraOta            = 79,
    GroupAlarm         = 112,
    PrivateApp         = 256,
    AtakForwarder      = 257,
}

/// <summary>
/// The 16-byte Meshtastic LoRa packet header that precedes the (encrypted)
/// payload. All multi-byte fields are little-endian. Mirrors firmware
/// <c>PacketHeader</c>.
/// </summary>
public readonly struct MeshHeader
{
    public const int Size = 16;

    public uint To { get; init; }
    public uint From { get; init; }
    public uint PacketId { get; init; }
    public byte Flags { get; init; }
    public byte ChannelHash { get; init; }
    public byte NextHop { get; init; }
    public byte RelayNode { get; init; }

    public byte HopLimit => (byte)(Flags & 0x07);
    public bool WantAck   => (Flags & 0x08) != 0;
    public bool ViaMqtt   => (Flags & 0x10) != 0;
    public byte HopStart  => (byte)((Flags >> 5) & 0x07);

    public bool IsBroadcast => To == 0xFFFFFFFFu;

    public static bool TryParse(ReadOnlySpan<byte> p, out MeshHeader header)
    {
        header = default;
        if (p.Length < Size) return false;
        header = new MeshHeader
        {
            To          = ReadU32(p, 0),
            From        = ReadU32(p, 4),
            PacketId    = ReadU32(p, 8),
            Flags       = p[12],
            ChannelHash = p[13],
            NextHop     = p[14],
            RelayNode   = p[15],
        };
        return true;
    }

    private static uint ReadU32(ReadOnlySpan<byte> p, int off) =>
        (uint)(p[off] | p[off + 1] << 8 | p[off + 2] << 16 | p[off + 3] << 24);

    public string FromId => $"!{From:x8}";
    public string ToId   => IsBroadcast ? "^all" : $"!{To:x8}";
}
