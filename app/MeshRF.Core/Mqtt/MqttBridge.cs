// SPDX-License-Identifier: GPL-3.0-or-later
using Meshtastic.Protobufs;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Extensions.ManagedClient;
using MQTTnet.Packets;

namespace MeshRF.Mqtt;

/// <summary>Effective configuration applied to a <see cref="MqttBridge"/>.
/// Values here are already resolved (see <see cref="MqttPolicy"/>
/// for the "empty means firmware default" rules) except where noted.</summary>
public sealed record MqttBridgeOptions(
    bool Enabled,
    string Address,
    string Username,
    string Password,
    bool TlsEnabled,
    string RootTopic,
    IReadOnlyList<string> DownlinkChannelNames,
    bool AnyDownlinkEnabled,
    bool JsonEnabled = false)
{
    public static readonly MqttBridgeOptions Disabled = new(
        false, string.Empty, string.Empty, string.Empty, false, string.Empty,
        Array.Empty<string>(), false, false);

    /// <summary>Value equality that treats <see cref="DownlinkChannelNames"/>
    /// as an unordered set rather than by reference, so re-applying
    /// equivalent options (e.g. after an unrelated settings save) is a no-op
    /// instead of forcing an unnecessary reconnect.</summary>
    public bool IsEquivalentTo(MqttBridgeOptions? other)
    {
        if (other is null) return false;
        return Enabled == other.Enabled
            && string.Equals(Address, other.Address, StringComparison.Ordinal)
            && string.Equals(Username, other.Username, StringComparison.Ordinal)
            && string.Equals(Password, other.Password, StringComparison.Ordinal)
            && TlsEnabled == other.TlsEnabled
            && string.Equals(RootTopic, other.RootTopic, StringComparison.Ordinal)
            && AnyDownlinkEnabled == other.AnyDownlinkEnabled
            && JsonEnabled == other.JsonEnabled
            && DownlinkChannelNames.ToHashSet(StringComparer.OrdinalIgnoreCase)
                   .SetEquals(other.DownlinkChannelNames);
    }
}

/// <summary>
/// Thin wrapper around MQTTnet's managed client for the Meshtastic MQTT
/// bridge (uplink/downlink). Handles connect/reconnect (via MQTTnet's
/// built-in managed client, which mirrors firmware's own reconnect-with-
/// backoff + offline publish queue), broker subscriptions for
/// downlink-enabled channels, and raw ServiceEnvelope publish/receive.
///
/// This class does no Meshtastic protocol interpretation beyond parsing the
/// ServiceEnvelope wrapper — deciding whether to accept/decrypt/inject a
/// received envelope, and what to publish for an outgoing packet, is
/// <see cref="MqttPolicy"/>'s and the caller's job. It is UI-framework
/// agnostic and lives here rather than in the app so it stays testable on its
/// own.
///
/// All events fire on MQTTnet's internal worker thread(s), not the UI
/// thread — callers must marshal to the UI dispatcher themselves.
/// </summary>
public sealed class MqttBridge : IDisposable
{
    private readonly MqttFactory _factory = new();
    private readonly string _clientId = "meshrf-" + Guid.NewGuid().ToString("N")[..12];
    private readonly object _lock = new();
    private IManagedMqttClient? _client;
    private MqttBridgeOptions _appliedOptions = MqttBridgeOptions.Disabled;
    private bool _disposed;

    /// <summary>Fires with true when the broker connection is established,
    /// false when it drops.</summary>
    public event Action<bool>? ConnectionChanged;

    /// <summary>Fires with a short human-readable status string, suitable for
    /// display in the MQTT settings window.</summary>
    public event Action<string>? StatusChanged;

    /// <summary>Fires for every successfully-parsed ServiceEnvelope received
    /// on a subscribed topic. The caller is responsible for all downlink
    /// policy/decryption/injection decisions.</summary>
    public event Action<ServiceEnvelope>? EnvelopeReceived;

    /// <summary>Fires for every message received on a JSON topic
    /// ("&lt;root&gt;/2/json/..."), raw topic and UTF-8 payload text. The
    /// caller is responsible for all downlink command validation/parsing.</summary>
    public event Action<string, string>? JsonMessageReceived;

    public bool IsConnected => _client?.IsConnected == true;

    /// <summary>
    /// Reconfigures the bridge. No-op if <paramref name="options"/> is
    /// equivalent to what's already applied. Connecting/reconnecting happens
    /// asynchronously in the background; use <see cref="StatusChanged"/> /
    /// <see cref="ConnectionChanged"/> to observe the outcome.
    /// </summary>
    public void ApplyOptions(MqttBridgeOptions options)
    {
        lock (_lock)
        {
            if (_disposed) return;
            if (_appliedOptions.IsEquivalentTo(options)) return;
            _appliedOptions = options;
        }
        _ = ApplyOptionsAsync(options);
    }

    private async Task ApplyOptionsAsync(MqttBridgeOptions options)
    {
        try
        {
            await StopInternalAsync().ConfigureAwait(false);

            if (!options.Enabled)
            {
                StatusChanged?.Invoke("Disabled");
                return;
            }

            var client = _factory.CreateManagedMqttClient();
            client.ConnectedAsync += _ =>
            {
                ConnectionChanged?.Invoke(true);
                StatusChanged?.Invoke("Connected");
                return Task.CompletedTask;
            };
            client.DisconnectedAsync += e =>
            {
                ConnectionChanged?.Invoke(false);
                StatusChanged?.Invoke(e.ClientWasConnected ? "Disconnected" : "Not connected");
                return Task.CompletedTask;
            };
            client.ConnectingFailedAsync += e =>
            {
                StatusChanged?.Invoke($"Connect failed: {e.Exception?.Message ?? "unknown error"}");
                return Task.CompletedTask;
            };
            client.ApplicationMessageReceivedAsync += OnMessageReceivedAsync;

            var host = MqttPolicy.EffectiveHost(options.Address);
            var port = MqttPolicy.EffectivePort(options.Address, options.TlsEnabled);
            var username = MqttPolicy.EffectiveUsername(options.Username);
            var password = MqttPolicy.EffectivePassword(options.Password);

            var clientOptionsBuilder = new MqttClientOptionsBuilder()
                .WithTcpServer(host, port)
                .WithCredentials(username, password)
                .WithClientId(_clientId)
                .WithCleanSession();
            if (options.TlsEnabled)
                clientOptionsBuilder.WithTlsOptions(_ => { }); // default (validated) certificate handling

            var managedOptions = new ManagedMqttClientOptionsBuilder()
                .WithClientOptions(clientOptionsBuilder.Build())
                .WithAutoReconnectDelay(TimeSpan.FromSeconds(5))
                .WithMaxPendingMessages(16) // mirrors firmware's MAX_MQTT_QUEUE
                .Build();

            lock (_lock) { _client = client; }

            StatusChanged?.Invoke("Connecting…");
            await client.StartAsync(managedOptions).ConfigureAwait(false);
            await ResubscribeAsync(client, options).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            StatusChanged?.Invoke($"Error: {ex.Message}");
        }
    }

    private Task OnMessageReceivedAsync(MqttApplicationMessageReceivedEventArgs args)
    {
        var topic = args.ApplicationMessage.Topic ?? string.Empty;
        MqttBridgeOptions options;
        lock (_lock) options = _appliedOptions;

        if (topic.StartsWith(MqttPolicy.JsonTopicPrefix(options.RootTopic), StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var json = System.Text.Encoding.UTF8.GetString(args.ApplicationMessage.PayloadSegment);
                JsonMessageReceived?.Invoke(topic, json);
            }
            catch
            {
                // Malformed JSON payload — ignore rather than crash the MQTT worker thread.
            }
            return Task.CompletedTask;
        }

        try
        {
            var envelope = ServiceEnvelope.Parser.ParseFrom(args.ApplicationMessage.PayloadSegment);
            EnvelopeReceived?.Invoke(envelope);
        }
        catch
        {
            // Malformed envelope from the broker (wrong topic, foreign
            // publisher, corrupt payload) — ignore rather than crash the
            // MQTT worker thread.
        }
        return Task.CompletedTask;
    }

    private static async Task ResubscribeAsync(IManagedMqttClient client, MqttBridgeOptions options)
    {
        var filters = new List<MqttTopicFilter>();
        foreach (var name in options.DownlinkChannelNames)
        {
            filters.Add(new MqttTopicFilterBuilder()
                .WithTopic(MqttPolicy.DownlinkSubscribeTopic(options.RootTopic, name))
                .Build());
            if (options.JsonEnabled)
                filters.Add(new MqttTopicFilterBuilder()
                    .WithTopic(MqttPolicy.JsonDownlinkSubscribeTopic(options.RootTopic, name))
                    .Build());
        }
        if (options.AnyDownlinkEnabled)
            filters.Add(new MqttTopicFilterBuilder()
                .WithTopic(MqttPolicy.PkiDownlinkSubscribeTopic(options.RootTopic))
                .Build());

        if (filters.Count > 0)
            await client.SubscribeAsync(filters).ConfigureAwait(false);
    }

    /// <summary>Fires once per accepted publish, with the topic and payload
    /// size. Every outgoing message goes through <see cref="Publish"/>, so
    /// hooking this is enough to log all MQTT sends in one place. Fires on the
    /// caller's thread — marshal to the UI dispatcher yourself.</summary>
    public event Action<string, int>? Published;

    /// <summary>Enqueues a publish (fire-and-forget; the managed client
    /// queues it while offline and sends once connected, up to the configured
    /// pending-message limit). Safe to call even when disabled/disconnected —
    /// it's simply dropped in that case.</summary>
    public void Publish(string topic, byte[] payload)
    {
        IManagedMqttClient? client;
        lock (_lock) client = _client;
        // Dropped rather than queued when there's no client, so this is
        // deliberately below the null check — nothing was sent.
        if (client is null) return;

        Published?.Invoke(topic, payload.Length);

        var message = new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(payload)
            .WithRetainFlag(false)
            .Build();
        _ = EnqueueSafeAsync(client, message);
    }

    private static async Task EnqueueSafeAsync(IManagedMqttClient client, MqttApplicationMessage message)
    {
        try { await client.EnqueueAsync(message).ConfigureAwait(false); }
        catch { /* best-effort */ }
    }

    private async Task StopInternalAsync()
    {
        IManagedMqttClient? client;
        lock (_lock) { client = _client; _client = null; }
        if (client is null) return;

        try { await client.StopAsync().ConfigureAwait(false); }
        catch { /* best-effort */ }
        client.Dispose();
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
        }
        _ = StopInternalAsync();
    }
}
