// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text;
using MeshRF.Channels;

namespace MeshRF.Mesh;

/// <summary>
/// Builds an on-air Meshtastic LoRa frame (16-byte L1 header + AES-CTR
/// encrypted <c>Data</c> protobuf) — the transmit counterpart to
/// <see cref="MeshDecoder"/>. The returned bytes are the raw payload handed to
/// the native LoRa PHY encoder (<c>mrf_core_transmit</c>), which adds the
/// preamble/sync/SFD/FEC and modulates the chirps.
///
/// Mirrors firmware <c>Router::send</c> / <c>PacketHeader</c>: the inner
/// <c>Data</c> sub-message (portnum + payload) is encrypted with the channel's
/// effective key using a per-packet CTR nonce derived from the sender node and
/// packet id, then prefixed with the 16-byte header.
/// </summary>
public static class MeshEncoder
{
    // Data.bitfield (field 9) bit 0: gateways may uplink this packet to the
    // public MQTT broker. Mirrors firmware BITFIELD_OK_TO_MQTT_MASK.
    private const ulong BitfieldOkToMqtt = 0x01;
    /// <summary>
    /// Encode a frame carrying <paramref name="port"/> + <paramref name="payload"/>.
    /// </summary>
    /// <param name="channel">Channel whose key/hash are used.</param>
    /// <param name="from">Sender node number.</param>
    /// <param name="to">Destination node number (0xFFFFFFFF = broadcast).</param>
    /// <param name="packetId">Sender's packet id (also the CTR nonce seed).</param>
    /// <param name="port">Application port number.</param>
    /// <param name="payload">Application payload bytes (the <c>Data.payload</c>).</param>
    /// <param name="hopLimit">Hops remaining (0..7). Also stored as hop_start.</param>
    /// <param name="wantAck">Request an ACK.</param>
    /// <param name="okToMqtt">Set Data.bitfield ok_to_mqtt so gateways may
    /// uplink this packet to the public MQTT broker.</param>
    public static byte[] Encode(ChannelConfig channel,
                                uint from,
                                uint to,
                                uint packetId,
                                PortNum port,
                                ReadOnlySpan<byte> payload,
                                byte hopLimit = 3,
                                bool wantAck = false,
                                bool wantResponse = false,
                                uint requestId = 0,
                                bool okToMqtt = false)
    {
        ArgumentNullException.ThrowIfNull(channel);

        // 1. Build the Data sub-message: field 1 = portnum, field 2 = payload,
        //    field 3 = want_response, field 6 = request_id (fixed32),
        //    field 9 = bitfield (bit 0 = ok_to_mqtt).
        var data = new ProtoWriter();
        data.WriteVarintField(1, (ulong)port);
        if (!payload.IsEmpty)
            data.WriteBytesField(2, payload);
        if (wantResponse)
            data.WriteVarintField(3, 1);
        if (requestId != 0)
            data.WriteFixed32Field(6, requestId);
        if (okToMqtt)
            data.WriteVarintField(9, BitfieldOkToMqtt);
        var plain = data.ToArray();

        // 2. Encrypt with the channel's effective key. An empty key means the
        //    channel is unencrypted (the frame carries the plaintext Data).
        var key = channel.EffectiveKey;
        byte[] cipher = (key.Length == 16 || key.Length == 32)
            ? MeshCrypto.Ctr(plain, key, from, packetId)
            : plain;

        // 3. Assemble the 16-byte L1 header + ciphertext.
        var frame = new byte[MeshHeader.Size + cipher.Length];
        WriteU32(frame, 0, to);
        WriteU32(frame, 4, from);
        WriteU32(frame, 8, packetId);
        frame[12] = PackFlags(hopLimit, wantAck);
        frame[13] = channel.Hash;
        frame[14] = 0x00;                 // next_hop (0 = no specific relay)
        frame[15] = (byte)(from & 0xFF);  // relay_node = our node id low byte
        cipher.CopyTo(frame, MeshHeader.Size);
        return frame;
    }

    /// <summary>Encode a TEXT_MESSAGE_APP frame carrying UTF-8 <paramref name="text"/>.</summary>
    public static byte[] EncodeTextMessage(ChannelConfig channel,
                                           uint from,
                                           uint packetId,
                                           string text,
                                           uint to = 0xFFFFFFFFu,
                                           byte hopLimit = 3,
                                           bool wantAck = false,
                                           bool okToMqtt = false)
        => Encode(channel, from, to, packetId, PortNum.TextMessage,
                  Encoding.UTF8.GetBytes(text), hopLimit, wantAck,
                  okToMqtt: okToMqtt);
    /// <summary>
    /// Encode a PKC (public-key) direct message addressed to a single peer,
    /// mirroring firmware <c>perhapsEncode</c>'s PKI path. The <c>Data</c>
    /// sub-message is sealed with X25519 + AES-CCM using our private key and the
    /// peer's public key (not a channel PSK), the channel-hash byte is forced to
    /// <c>0x00</c>, and the 12-byte PKC overhead (tag + extra nonce) is appended.
    /// Such frames decode on modern Meshtastic nodes that have our public key.
    /// </summary>
    /// <param name="from">Sender node number.</param>
    /// <param name="to">Destination node number (must be a unicast address).</param>
    /// <param name="packetId">Sender's packet id (also seeds the CCM nonce).</param>
    /// <param name="port">Application port number.</param>
    /// <param name="payload">Application payload bytes.</param>
    /// <param name="myPrivateKey">Our 32-byte X25519 private key.</param>
    /// <param name="peerPublicKey">The peer's 32-byte X25519 public key.</param>
    /// <param name="hopLimit">Hops remaining (0..7). Also stored as hop_start.</param>
    /// <param name="wantAck">Request an ACK.</param>
    public static byte[] EncodePkc(uint from,
                                   uint to,
                                   uint packetId,
                                   PortNum port,
                                   ReadOnlySpan<byte> payload,
                                   byte[] myPrivateKey,
                                   byte[] peerPublicKey,
                                   byte hopLimit = 3,
                                   bool wantAck = false,
                                   bool wantResponse = false,
                                   uint requestId = 0,
                                   bool okToMqtt = false)
    {
        ArgumentNullException.ThrowIfNull(myPrivateKey);
        ArgumentNullException.ThrowIfNull(peerPublicKey);
        if (to == 0xFFFFFFFFu)
            throw new ArgumentException("PKC packets must be addressed to a single node.", nameof(to));

        // 1. Build the Data sub-message: field 1 = portnum, field 2 = payload,
        //    field 3 = want_response, field 6 = request_id (fixed32),
        //    field 9 = bitfield (bit 0 = ok_to_mqtt).
        var data = new ProtoWriter();
        data.WriteVarintField(1, (ulong)port);
        if (!payload.IsEmpty)
            data.WriteBytesField(2, payload);
        if (wantResponse)
            data.WriteVarintField(3, 1);
        if (requestId != 0)
            data.WriteFixed32Field(6, requestId);
        if (okToMqtt)
            data.WriteVarintField(9, BitfieldOkToMqtt);
        var plain = data.ToArray();

        // 2. Seal with X25519 + AES-CCM (ciphertext || 8-byte tag || 4-byte nonce).
        byte[] sealedPayload = MeshCrypto.PkcEncrypt(plain, myPrivateKey, peerPublicKey, from, packetId);

        // 3. Assemble the 16-byte L1 header + sealed payload. Channel hash is 0x00
        //    to signal PKC to the receiver (firmware sets p->channel = 0).
        var frame = new byte[MeshHeader.Size + sealedPayload.Length];
        WriteU32(frame, 0, to);
        WriteU32(frame, 4, from);
        WriteU32(frame, 8, packetId);
        frame[12] = PackFlags(hopLimit, wantAck);
        frame[13] = 0x00;                 // channel hash 0 = PKC
        frame[14] = 0x00;                 // next_hop
        frame[15] = (byte)(from & 0xFF);  // relay_node = our node id low byte
        sealedPayload.CopyTo(frame, MeshHeader.Size);
        return frame;
    }

    /// <summary>Encode a PKC direct text message (TEXT_MESSAGE_APP) for a peer.</summary>
    public static byte[] EncodePkcTextMessage(uint from,
                                              uint to,
                                              uint packetId,
                                              string text,
                                              byte[] myPrivateKey,
                                              byte[] peerPublicKey,
                                              byte hopLimit = 3,
                                              bool wantAck = false,
                                              bool okToMqtt = false)
        => EncodePkc(from, to, packetId, PortNum.TextMessage,
                     Encoding.UTF8.GetBytes(text), myPrivateKey, peerPublicKey,
                     hopLimit, wantAck, okToMqtt: okToMqtt);

    /// <summary>
    /// Encode a NODEINFO_APP frame carrying our <c>User</c> protobuf — the
    /// node-info broadcast firmware sends so peers learn our id/name/role.
    /// Mirrors meshtastic <c>User</c>: field 1 = id ("!xxxxxxxx"), field 2 =
    /// long_name, field 3 = short_name, field 5 = hw_model, field 7 = role,
    /// field 8 = public_key. Optional fields are omitted when unset.
    /// </summary>
    /// <param name="channel">Channel whose key/hash are used.</param>
    /// <param name="from">Sender node number.</param>
    /// <param name="packetId">Sender's packet id (also the CTR nonce seed).</param>
    /// <param name="longName">Our long display name.</param>
    /// <param name="shortName">Our short (≤4 char) name.</param>
    /// <param name="hwModel">HardwareModel enum value (0 = UNSET, omitted).</param>
    /// <param name="role">DeviceConfig.Role enum value (0 = CLIENT default).</param>
    /// <param name="publicKey">32-byte PKC public key, or empty to omit.</param>
    /// <param name="to">Destination (0xFFFFFFFF = broadcast).</param>
    /// <param name="hopLimit">Hops remaining (0..7).</param>
    /// <param name="wantResponse">Set Data.want_response so the recipient of a
    /// directed request replies with their own NodeInfo (used to learn a peer's
    /// public key before sending a PKC direct message).</param>
    public static byte[] EncodeNodeInfo(ChannelConfig channel,
                                        uint from,
                                        uint packetId,
                                        string longName,
                                        string shortName,
                                        uint hwModel = 0,
                                        uint role = 0,
                                        ReadOnlySpan<byte> publicKey = default,
                                        uint to = 0xFFFFFFFFu,
                                        byte hopLimit = 3,
                                        bool wantResponse = false,
                                        bool okToMqtt = false)
    {
        var user = new ProtoWriter();
        user.WriteStringField(1, $"!{from:x8}");          // id
        user.WriteStringField(2, longName ?? string.Empty); // long_name
        user.WriteStringField(3, shortName ?? string.Empty);// short_name
        if (hwModel != 0)
            user.WriteVarintField(5, hwModel);              // hw_model
        if (role != 0)
            user.WriteVarintField(7, role);                 // role
        if (!publicKey.IsEmpty)
            user.WriteBytesField(8, publicKey);             // public_key

        return Encode(channel, from, to, packetId, PortNum.NodeInfo,
                      user.ToArray(), hopLimit, wantAck: false,
                      wantResponse: wantResponse, okToMqtt: okToMqtt);
    }

    /// <summary>
    /// Broadcast our location (POSITION_APP <c>Position</c> protobuf). The
    /// channel's <paramref name="precisionBits"/> fuzzes the transmitted
    /// coordinates exactly like firmware <c>PositionModule</c>: keep the top
    /// N bits of each lat/lon i32, add a half-bit for rounding, and advertise
    /// the precision so receivers know the uncertainty. 0 disables sharing,
    /// 32 sends full precision.
    /// </summary>
    public static byte[] EncodePosition(ChannelConfig channel,
                                        uint from,
                                        uint packetId,
                                        double latitude,
                                        double longitude,
                                        int? altitudeM = null,
                                        byte precisionBits = 32,
                                        uint to = 0xFFFFFFFFu,
                                        byte hopLimit = 3,
                                        bool wantResponse = false,
                                        bool okToMqtt = false)
    {
        int latI = (int)Math.Round(latitude / 1e-7);
        int lonI = (int)Math.Round(longitude / 1e-7);

        if (precisionBits > 0 && precisionBits < 32)
        {
            latI = (int)((uint)latI & (uint.MaxValue << (32 - precisionBits)));
            lonI = (int)((uint)lonI & (uint.MaxValue << (32 - precisionBits)));
            latI += 1 << (31 - precisionBits);
            lonI += 1 << (31 - precisionBits);
        }

        var pos = new ProtoWriter();
        pos.WriteFixed32Field(1, (uint)latI);                 // latitude_i (sfixed32)
        pos.WriteFixed32Field(2, (uint)lonI);                 // longitude_i (sfixed32)
        if (altitudeM is int alt)
            pos.WriteVarintField(3, (ulong)(long)alt);        // altitude (int32)
        pos.WriteFixed32Field(4, (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds()); // time
        pos.WriteVarintField(22, precisionBits == 0 ? 32u : precisionBits);        // precision_bits

        return Encode(channel, from, to, packetId, PortNum.Position,
                      pos.ToArray(), hopLimit, wantAck: false,
                      wantResponse: wantResponse, okToMqtt: okToMqtt);
    }

    /// <summary>
    /// Encode a ROUTING_APP acknowledgement (or negative-ack) for a received
    /// packet, mirroring firmware <c>Router::sendAckNak</c>. The Routing
    /// sub-message carries <c>error_reason</c> (0 = ACK/NONE, non-zero = NAK
    /// reason) and the outer Data references the original packet via
    /// <c>request_id</c>. Channel-PSK encrypted (used to ack legacy frames).
    /// </summary>
    /// <param name="channel">Channel whose key/hash are used.</param>
    /// <param name="from">Our node number.</param>
    /// <param name="to">The original sender we are acking.</param>
    /// <param name="packetId">A fresh packet id for this ack frame.</param>
    /// <param name="requestId">The packet id being acked.</param>
    /// <param name="errorReason">0 = ACK, non-zero = NAK reason.</param>
    /// <param name="hopLimit">Hops remaining (0..7).</param>
    public static byte[] EncodeRouting(ChannelConfig channel,
                                       uint from,
                                       uint to,
                                       uint packetId,
                                       uint requestId,
                                       uint errorReason = 0,
                                       byte hopLimit = 3)
        => Encode(channel, from, to, packetId, PortNum.Routing,
                  BuildRouting(errorReason), hopLimit, wantAck: false,
                  wantResponse: false, requestId: requestId);

    /// <summary>Encode a PKC (public-key) ROUTING_APP ack/nak for a received PKC
    /// direct message, sealed back to the original sender.</summary>
    public static byte[] EncodePkcRouting(uint from,
                                          uint to,
                                          uint packetId,
                                          uint requestId,
                                          byte[] myPrivateKey,
                                          byte[] peerPublicKey,
                                          uint errorReason = 0,
                                          byte hopLimit = 3)
        => EncodePkc(from, to, packetId, PortNum.Routing,
                     BuildRouting(errorReason), myPrivateKey, peerPublicKey,
                     hopLimit, wantAck: false, wantResponse: false,
                     requestId: requestId);

    // Routing protobuf: oneof variant { error_reason = 3 (varint) }. The ACK
    // case (error 0) is serialised explicitly so the receiver sees the oneof.
    private static byte[] BuildRouting(uint errorReason)
    {
        var r = new ProtoWriter();
        r.WriteVarintField(3, errorReason);
        return r.ToArray();
    }

    // flags: [0..2]=hop_limit [3]=want_ack [4]=via_mqtt [5..7]=hop_start.
    // hop_start mirrors hop_limit at send time (firmware sets them equal).
    private static byte PackFlags(byte hopLimit, bool wantAck)
    {
        byte hl = (byte)(hopLimit & 0x07);
        byte flags = hl;
        if (wantAck) flags |= 0x08;
        flags |= (byte)(hl << 5); // hop_start = hop_limit
        return flags;
    }

    private static void WriteU32(byte[] buf, int off, uint v)
    {
        buf[off] = (byte)(v & 0xFF);
        buf[off + 1] = (byte)((v >> 8) & 0xFF);
        buf[off + 2] = (byte)((v >> 16) & 0xFF);
        buf[off + 3] = (byte)((v >> 24) & 0xFF);
    }
}
