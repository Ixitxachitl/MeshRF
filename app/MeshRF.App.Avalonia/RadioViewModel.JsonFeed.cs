// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using MeshRF.Mesh;

namespace MeshRF.AvaloniaApp;

/// <summary>One decoded packet in the raw feed: a one-line header for the
/// collapsed row, the exact JSON (what gets copied or exported), and a
/// display variant that wraps long hex.</summary>
public sealed record DecodedPacketJsonEntry(string Header, string Json, string DisplayJson);

/// <summary>
/// The raw decoded-packet JSON feed, ported from MeshRF.App. Every packet that
/// decodes is serialised in full — header fields, addressing, signal, and the
/// decoded payload — so traffic can be inspected without a separate sniffer.
/// </summary>
public partial class RadioViewModel
{
    private const int MaxDecodedPacketJsonEntries = 500;

    private static readonly JsonSerializerOptions FeedJsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    // Long unbroken hex runs (payload dumps) have no break opportunity, so a
    // wrapping text box renders them as one enormous line. Zero-width spaces
    // are injected for display only; the copied/exported JSON stays exact.
    private static readonly Regex LongHexJsonStringRegex =
        new("\"(?<hex>[0-9A-Fa-f]{64,})\"", RegexOptions.Compiled);

    public ObservableCollection<DecodedPacketJsonEntry> DecodedPacketJsonEntries { get; } = new();

    /// <summary>Serialises one decoded packet into the feed. Called for every
    /// decode, so it stays cheap and bounded.</summary>
    public void AppendDecodedPacketJson(MeshHeader header, MeshDecodeResult result,
                                        long rxEpoch, float? snrDb, float? packetRssiDbm,
                                        byte hopsAway, string summary)
    {
        try
        {
            var payload = new
            {
                time = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss.fffzzz", CultureInfo.InvariantCulture),
                rx_epoch = rxEpoch,
                summary,
                packet = new
                {
                    from = header.From,
                    from_id = header.FromId,
                    to = header.To,
                    to_id = header.ToId,
                    packet_id = header.PacketId,
                    via_mqtt = header.ViaMqtt,
                    hops_away = hopsAway,
                    rssi_dbm = packetRssiDbm,
                    snr_db = snrDb,
                },
                decoded = new
                {
                    header = new
                    {
                        to = header.To,
                        from = header.From,
                        packet_id = header.PacketId,
                        flags = header.Flags,
                        channel_hash = header.ChannelHash,
                        hop_limit = header.HopLimit,
                        want_ack = header.WantAck,
                        via_mqtt = header.ViaMqtt,
                        hop_start = header.HopStart,
                        is_broadcast = header.IsBroadcast,
                        from_id = header.FromId,
                        to_id = header.ToId,
                    },
                    channel = result.ChannelName,
                    port = result.Port.ToString(),
                    text = result.Text,
                    want_response = result.WantResponse,
                    request_id = result.RequestId,
                    reply_id = result.ReplyId,
                    emoji = result.Emoji,
                    payload_hex = result.AppPayload.Length > 0
                        ? Convert.ToHexString(result.AppPayload) : null,
                },
            };

            string json = JsonSerializer.Serialize(payload, FeedJsonOptions);
            string display = LongHexJsonStringRegex.Replace(json, m =>
            {
                var hex = m.Groups["hex"].Value;
                var sb = new StringBuilder(hex.Length + hex.Length / 32);
                for (int i = 0; i < hex.Length; i += 32)
                {
                    int len = Math.Min(32, hex.Length - i);
                    sb.Append(hex, i, len);
                    if (i + len < hex.Length) sb.Append('​');
                }
                return "\"" + sb + "\"";
            });
            string headerText =
                $"[{DateTime.Now.ToString("M/d/yyyy h:mm:ss tt", CultureInfo.CurrentCulture)}] {summary.Trim()}";

            Dispatcher.UIThread.Post(() =>
            {
                DecodedPacketJsonEntries.Add(new DecodedPacketJsonEntry(headerText, json, display));
                while (DecodedPacketJsonEntries.Count > MaxDecodedPacketJsonEntries)
                    DecodedPacketJsonEntries.RemoveAt(0);
            });
        }
        catch (Exception ex)
        {
            // A feed entry is diagnostic; never let it break packet handling.
            StatusText = $"JSON feed error: {ex.Message}";
        }
    }

    [RelayCommand]
    private void ClearDecodedPacketJsonFeed() => DecodedPacketJsonEntries.Clear();

    /// <summary>The whole feed as newline-separated JSON documents — what both
    /// Copy and Export write.</summary>
    public string BuildDecodedPacketJsonFeedText() =>
        string.Join(Environment.NewLine, DecodedPacketJsonEntries.Select(e => e.Json));
}
