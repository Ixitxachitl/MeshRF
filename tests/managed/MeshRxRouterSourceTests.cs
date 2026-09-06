// SPDX-License-Identifier: GPL-3.0-or-later
using MeshRF.Channels;
using MeshRF.Mesh;
using MeshRF.Messages;
using Microsoft.Data.Sqlite;
using Xunit;

namespace MeshRF.Tests;

/// <summary>
/// The router hands every host call the source a frame was heard on, tries
/// the frame against that source's channel list alone, and offers MQTT
/// uplink for the primary's packets only.
/// </summary>
public sealed class MeshRxRouterSourceTests : IDisposable
{
    private readonly string _dir;
    private readonly MessageStore _messages;

    public MeshRxRouterSourceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "meshrf-router-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _messages = new MessageStore(Path.Combine(_dir, "messages.db"));
    }

    public void Dispose()
    {
        _messages.Dispose();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private sealed class InlineDispatcher : IUiDispatcher
    {
        public void Post(Action action) => action();
        public Task InvokeAsync(Action action) { action(); return Task.CompletedTask; }
    }

    /// <summary>A host that records what it was told and with which source.</summary>
    private sealed class RecordingHost : IMeshRxHost
    {
        public readonly Dictionary<string, List<ChannelConfig>> Lists = new();
        public readonly List<(string Call, RxSource Source)> Calls = new();

        public uint MyNodeNum => 0x22222222u;
        public byte[] MyPrivateKeyBytes => Array.Empty<byte>();
        public float CurrentRssiDbfs => -60f;

        public IReadOnlyList<ChannelConfig> ChannelsFor(RxSource source) =>
            Lists.TryGetValue(source.IsPrimary ? "" : source.PresetName, out var list) ? list : Array.Empty<ChannelConfig>();

        public string? GetStoredPublicKeyHex(uint nodeNum) => null;
        public void Log(string message) { }
        public void RecordSighting(uint fromNode, long rxEpoch, float? rssiDbm, float? snrDb, byte hopsAway, bool viaMqtt, RxSource source)
            => Calls.Add(("RecordSighting", source));
        public void MarkNodeDirty(uint nodeNum) { }
        public void OnOwnPacketHeard(MeshHeader header, MeshDecodeResult? ownDecode) { }
        public bool RememberUndecodedPacket(MeshHeader header) => true;
        public void HandleDuplicateForRelay(byte[] frame, MeshHeader header, MeshDecodeResult? result, float? snrDb, RxSource source)
            => Calls.Add(("HandleDuplicateForRelay", source));
        public void RelayIfEligible(byte[] frame, MeshHeader header, MeshDecodeResult? result, float? snrDb, RxSource source)
            => Calls.Add(("RelayIfEligible", source));
        public void UplinkIfEligible(byte[] frame, MeshHeader header, MeshDecodeResult? result, bool isFromUs, float? snrDb, float? rssiDbm)
            => Calls.Add(("UplinkIfEligible", null!));
        public void OnMessageDecoded(byte[] frame, MeshHeader header, MessageRecord record, MeshDecodeResult result,
                                     long rxEpoch, float? snrDb, float? packetRssiDbm, byte hopsAway, RxSource source)
            => Calls.Add(("OnMessageDecoded", source));
        public void OnDuplicateDecoded(MeshHeader header, MeshDecodeResult result, RxSource source)
            => Calls.Add(("OnDuplicateDecoded", source));
        public void OnUndecodedPacket(MeshHeader header, RxSource source)
            => Calls.Add(("OnUndecodedPacket", source));
    }

    private static ChannelConfig DefaultChannel(string preset, string name) =>
        new() { Preset = preset, Index = 0, Name = name, Role = ChannelRole.Primary, Psk = new byte[] { 0x01 } };

    private static (byte[] Frame, MeshHeader Header) TextFrame(ChannelConfig channel, uint packetId)
    {
        var frame = MeshEncoder.EncodeTextMessage(channel, 0x11111111u, packetId, "hello");
        Assert.True(MeshHeader.TryParse(frame, out var header));
        return (frame, header);
    }

    [Fact]
    public void ASecondaryPacketReachesEveryCallWithItsSourceAndNeverTheBroker()
    {
        var host = new RecordingHost();
        host.Lists["LongFast"] = new() { DefaultChannel("LongFast", "LongFast") };
        using var router = new MeshRxRouter(host, _messages, new InlineDispatcher());

        var source = new RxSource(1, LoraPreset.LongFast, false, 906.875);
        var (frame, header) = TextFrame(host.Lists["LongFast"][0], 1001);
        router.ProcessReceivedFrame(frame, header, snrDb: 5f, packetRssiDbm: -80f, source);

        Assert.Contains(host.Calls, c => c.Call == "RecordSighting" && c.Source == source);
        Assert.Contains(host.Calls, c => c.Call == "RelayIfEligible" && c.Source == source);
        Assert.Contains(host.Calls, c => c.Call == "OnMessageDecoded" && c.Source == source);
        Assert.DoesNotContain(host.Calls, c => c.Call == "UplinkIfEligible");

        // Its retransmission is a duplicate, still on the same source.
        router.ProcessReceivedFrame(frame, header, snrDb: 5f, packetRssiDbm: -80f, source);
        Assert.Contains(host.Calls, c => c.Call == "OnDuplicateDecoded" && c.Source == source);
        Assert.Contains(host.Calls, c => c.Call == "HandleDuplicateForRelay" && c.Source == source);
        Assert.DoesNotContain(host.Calls, c => c.Call == "UplinkIfEligible");
    }

    [Fact]
    public void ThePrimaryPacketIsOfferedToTheBroker()
    {
        var host = new RecordingHost();
        host.Lists[""] = new() { DefaultChannel("", "MediumFast") };
        using var router = new MeshRxRouter(host, _messages, new InlineDispatcher());

        var source = RxSource.Primary(LoraPreset.MediumFast, false, 913.125);
        var (frame, header) = TextFrame(host.Lists[""][0], 1002);
        router.ProcessReceivedFrame(frame, header, snrDb: 5f, packetRssiDbm: -80f, source);

        Assert.Contains(host.Calls, c => c.Call == "OnMessageDecoded" && c.Source == source);
        Assert.Contains(host.Calls, c => c.Call == "UplinkIfEligible");
    }

    [Fact]
    public void DecodeIsHandedTheSourcesListAlone()
    {
        // The LongFast list holds the channel; the primary's list is empty.
        var host = new RecordingHost();
        host.Lists["LongFast"] = new() { DefaultChannel("LongFast", "LongFast") };
        host.Lists[""] = new();
        using var router = new MeshRxRouter(host, _messages, new InlineDispatcher());

        var (frame, header) = TextFrame(host.Lists["LongFast"][0], 1003);

        var primary = RxSource.Primary(LoraPreset.MediumFast, false, 913.125);
        router.ProcessReceivedFrame(frame, header, snrDb: null, packetRssiDbm: null, primary);
        Assert.Contains(host.Calls, c => c.Call == "OnUndecodedPacket" && c.Source == primary);
        Assert.DoesNotContain(host.Calls, c => c.Call == "OnMessageDecoded");
        // Undecoded on the primary: still offered for uplink, as before.
        Assert.Contains(host.Calls, c => c.Call == "UplinkIfEligible");

        host.Calls.Clear();
        var longFast = new RxSource(1, LoraPreset.LongFast, false, 906.875);
        var (frame2, header2) = TextFrame(host.Lists["LongFast"][0], 1004);
        router.ProcessReceivedFrame(frame2, header2, snrDb: null, packetRssiDbm: null, longFast);
        Assert.Contains(host.Calls, c => c.Call == "OnMessageDecoded" && c.Source == longFast);
        Assert.DoesNotContain(host.Calls, c => c.Call == "UplinkIfEligible");
    }
}
