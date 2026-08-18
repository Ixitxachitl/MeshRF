// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text;
using MeshRF.Channels;
using MeshRF.Mesh;
using Xunit;

namespace MeshRF.Tests;

/// <summary>
/// Channel selection cases the decoder has to treat the way firmware
/// <c>perhapsDecode</c> plus <c>getKey</c> do: an unencrypted channel is
/// readable, a disabled one matches nothing.
/// </summary>
public class MeshDecoderChannelRoleTests
{
    private const uint From = 0x0A0B0C0Du;
    private const uint Id = 0x01020304u;

    /// <summary>Data protobuf: portnum = TEXT_MESSAGE_APP, payload = text.</summary>
    private static byte[] TextData(string message)
    {
        var text = Encoding.UTF8.GetBytes(message);
        var data = new List<byte> { 0x08, 0x01, 0x12, (byte)text.Length };
        data.AddRange(text);
        return data.ToArray();
    }

    private static byte[] Frame(byte channelHash, byte[] payload)
    {
        var frame = new byte[MeshHeader.Size + payload.Length];
        frame[0] = frame[1] = frame[2] = frame[3] = 0xFF; // to = broadcast
        BitConverter.GetBytes(From).CopyTo(frame, 4);
        BitConverter.GetBytes(Id).CopyTo(frame, 8);
        frame[12] = 0x03;
        frame[13] = channelHash;
        payload.CopyTo(frame, MeshHeader.Size);
        return frame;
    }

    [Fact]
    public void ChannelWithNoPskCarriesPlaintext()
    {
        // Firmware encryptPacket() is a no-op for a zero-length key, so the
        // Data protobuf goes out in the clear.
        var channel = new ChannelConfig
        {
            Index = 0,
            Name = "Open",
            Role = ChannelRole.Primary,
            Psk = new byte[] { 0x00 },
        };

        var result = MeshDecoder.Decode(Frame(channel.Hash, TextData("in the clear")), new[] { channel });

        Assert.NotNull(result);
        Assert.Equal("in the clear", result!.Text);
        Assert.Equal("Open", result.ChannelName);
    }

    [Fact]
    public void DisabledChannelDecodesNothing()
    {
        var channel = new ChannelConfig
        {
            Index = 0,
            Name = "Open",
            Role = ChannelRole.Primary,
            Psk = new byte[] { 0x00 },
        };
        var frame = Frame(channel.Hash, TextData("in the clear"));

        // Same channel, same frame: only the role changes.
        Assert.NotNull(MeshDecoder.Decode(frame, new[] { channel }));

        channel.Role = ChannelRole.Disabled;
        Assert.Null(MeshDecoder.Decode(frame, new[] { channel }));
    }

    [Fact]
    public void SecondaryWithNoPskDecodesWithThePrimaryKey()
    {
        var primary = new ChannelConfig
        {
            Index = 0,
            Name = "LongFast",
            Role = ChannelRole.Primary,
            Psk = new byte[] { 0x01 },
        };
        var secondary = new ChannelConfig
        {
            Index = 1,
            Name = "Alta",
            Role = ChannelRole.Secondary,
            Psk = Array.Empty<byte>(),
            PrimaryProvider = () => primary,
        };

        var cipher = MeshCrypto.Ctr(TextData("borrowed key"), ChannelConfig.DefaultPsk, From, Id);
        var result = MeshDecoder.Decode(Frame(secondary.Hash, cipher), new[] { primary, secondary });

        Assert.NotNull(result);
        Assert.Equal("borrowed key", result!.Text);
        Assert.Equal("Alta", result.ChannelName);
    }
}
