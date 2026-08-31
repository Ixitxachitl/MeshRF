// SPDX-License-Identifier: GPL-3.0-or-later
using MeshRF.Channels;
using MeshRF.Mesh;
using MeshRF.Nodes;
using Xunit;

namespace MeshRF.Tests;

/// <summary>
/// <c>User.macaddr</c> (field 4) is deprecated firmware-side: a node still puts
/// its real chip MAC on the wire at boot, but one answering out of a reloaded
/// NodeDB advertises six zero bytes instead. We keep the real one when it
/// arrives, treat the zero-filled placeholder as "not advertised", and never
/// let a later blank overwrite a MAC already on file.
/// </summary>
public class NodeInfoMacAddressTests
{
    private static ChannelConfig Channel() => new()
    {
        Index = 0,
        Name = "LongFast",
        Psk = new byte[] { 0x01 },
        Role = ChannelRole.Primary,
    };

    /// <summary>Builds a NODEINFO_APP frame carrying an explicit macaddr, which
    /// <see cref="MeshEncoder.EncodeNodeInfo"/> never writes for us.</summary>
    private static byte[] NodeInfoWithMac(uint from, byte[]? mac)
    {
        var user = new ProtoWriter();
        user.WriteStringField(1, $"!{from:x8}");
        user.WriteStringField(2, "Mac Node");
        user.WriteStringField(3, "MAC");
        if (mac is not null) user.WriteBytesField(4, mac);
        user.WriteVarintField(5, 43);
        return MeshEncoder.Encode(Channel(), from, 0xFFFFFFFFu, 1,
                                  PortNum.NodeInfo, user.ToArray());
    }

    private static MeshUser Decode(byte[]? mac)
    {
        var result = MeshDecoder.Decode(NodeInfoWithMac(0x1A2B3C4Du, mac), new[] { Channel() });
        Assert.NotNull(result?.User);
        return result!.User!;
    }

    [Fact]
    public void ParsesMacAddressAsLowercaseColonHex()
    {
        var user = Decode(new byte[] { 0xAC, 0x67, 0xB2, 0x01, 0x0F, 0xFF });

        Assert.Equal("ac:67:b2:01:0f:ff", user.MacAddress);
    }

    [Fact]
    public void AllZeroMacAddressReadsAsAbsent()
    {
        // What firmware's ConvertToUser() substitutes once the slim NodeDB
        // header has dropped the real value.
        Assert.Equal(string.Empty, Decode(new byte[6]).MacAddress);
    }

    [Fact]
    public void MissingMacAddressFieldReadsAsAbsent()
    {
        Assert.Equal(string.Empty, Decode(null).MacAddress);
    }

    [Fact]
    public void ShortMacAddressReadsAsAbsent()
    {
        Assert.Equal(string.Empty, Decode(new byte[] { 0xAC, 0x67 }).MacAddress);
    }

    [Fact]
    public void StorePersistsMacAddress()
    {
        using var store = new NodeStore(":memory:");
        store.Upsert(new NodeRecord { NodeNum = 7, MacAddress = "ac:67:b2:01:0f:ff" });

        var stored = store.Get(7)!;
        Assert.Equal("ac:67:b2:01:0f:ff", stored.MacAddress);
        Assert.True(stored.HasMacAddress);
    }

    [Fact]
    public void LaterBlankMacAddressKeepsTheStoredOne()
    {
        using var store = new NodeStore(":memory:");
        store.Upsert(new NodeRecord { NodeNum = 7, MacAddress = "ac:67:b2:01:0f:ff" });
        store.Upsert(new NodeRecord { NodeNum = 7, LongName = "Renamed" });

        Assert.Equal("ac:67:b2:01:0f:ff", store.Get(7)!.MacAddress);
    }

    [Fact]
    public void NodeWithNoMacAddressReportsNone()
    {
        using var store = new NodeStore(":memory:");
        store.Upsert(new NodeRecord { NodeNum = 7, LongName = "Quiet" });

        Assert.False(store.Get(7)!.HasMacAddress);
    }
}
