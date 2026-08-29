// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia.Controls;
using MeshRF.Scripting;

namespace MeshRF.AvaloniaApp;

/// <summary>One row of the reference tables: the syntax on the left, what it
/// does on the right.</summary>
/// <param name="Name">The key as it is written in a script.</param>
/// <param name="Description">What it means.</param>
public sealed record HelpRow(string Name, string Description);

/// <summary>
/// The script reference, opened from the Scripts window's Help button.
/// </summary>
/// <remarks>
/// The tables are built here rather than written out in the XAML so the ones
/// with a single source of truth stay honest: the placeholder list is read
/// straight from <see cref="ScriptPlaceholders"/>, and the default limits are
/// read from <see cref="ScriptLimits"/>, so neither can drift from what the
/// parser actually accepts.
/// </remarks>
public partial class ScriptHelpWindow : Window
{
    public ScriptHelpWindow()
    {
        InitializeComponent();
        DataContext = this;
    }

    public string ScriptsFolder => ScriptLibrary.DefaultDirectory;

    public string ExampleScript =>
        """
        enabled: true
        alias: Answer !ping with a signal report

        trigger:
          - command: ping

        condition:
          - scope: direct
          - snr_above: -12

        action:
          - reply: "pong — {snr} dB over {hops} hops"

        limits:
          cooldown: 60s
          max_per_hour: 6
        """;

    public IReadOnlyList<HelpRow> Triggers { get; } =
    [
        new("command: ping", "Message starts with !ping. The rest becomes {args}."),
        new("text: \"^!wx\\\\b\"", "Message matches this regular expression. Capture groups become {cap1}, {cap2}, …"),
        new("  ignore_case: false", "Option on a text: trigger. Matching ignores case unless you turn this off."),
        new("new_node: true", "A node MeshRF has never heard before is decoded for the first time."),
        new("reaction: 👍", "An emoji tapback lands on one of your messages. Use 'any' for any emoji."),
        new("every: 4h", "Fires on a fixed interval. Minimum 1m."),
        new("at: \"08:00\"", "Fires once a day at this local time."),
    ];

    public IReadOnlyList<HelpRow> Conditions { get; } =
    [
        new("scope: direct", "Only direct messages. Also: channel, any, and primary for the primary channel whatever it is named."),
        new("channel: [LongFast]", "Only these channels. One name or a list. {primary} names the primary by role, so channel: \"{primary}\" is scope: primary — except that it also matches a legacy direct message, which carries the channel it was encrypted with. A list may mix the two: [\"{primary}\", Alerts]."),
        new("not_channel: [Test]", "Every channel except these. Direct messages arrive on no channel, so they are never excluded — use scope: with it to narrow that."),
        new("from: [\"!a1b2c3d4\"]", "Only these senders."),
        new("not_from: [\"!deadbeef\"]", "Never these senders."),
        new("snr_above: -12", "Only packets heard at better than this signal-to-noise ratio, in dB."),
        new("hops_below: 3", "Only packets that travelled fewer than this many hops. 0–7."),
        new("between: \"08:00-22:00\"", "Only inside this local-time window. A window may wrap past midnight."),
        new("favorite: true", "Only senders marked favourite in the Nodes table."),
        new("has_key: true", "Only senders whose public key is known, so a reply can be PKC-sealed."),
    ];

    public IReadOnlyList<HelpRow> Actions { get; } =
    [
        new("reply: \"text\"", "Answer in the conversation the trigger arrived on, threaded under the message. To answer somewhere else — another channel, or the sender of a channel message by DM — use send: instead."),
        new("send:", "Send somewhere specific. Takes the indented keys below."),
        new("  to: \"{from.id}\"", "Destination node. A node id, or a placeholder that becomes one."),
        new("  channel: LongFast", "Destination channel instead of a node. Use one of to:/channel:, not both. Neither means the primary, and channel: {primary} says so out loud — the only way to name a primary that has no name of its own. Braces, so it cannot be mistaken for a channel called \"primary\"."),
        new("  text: \"…\"", "The message body. Required."),
        new("  reply_link: true", "Thread the message under the one that triggered it."),
        new("  hops: 0", "Hop limit for this one message, 0-7, instead of the app-wide setting. 0 reaches direct neighbours only and is never relayed — the right answer for something local, and the cheapest thing a script can transmit. Raise it only for a message that genuinely has to cross the mesh."),
        new("http:", "Call a REST endpoint and keep the answer for a later action. Takes the indented keys below."),
        new("  url: \"https://…\"", "The endpoint. Placeholders in it are percent-encoded, so a message containing & or a space cannot rewrite the request. Required, and must be https:// or http://."),
        new("  method: GET", "GET (default), POST or PUT."),
        new("  credential: weather", "Authenticate using a key stored under Credentials. The value never appears in the script."),
        new("  credential: [a, b]", "Or several, for an API wanting an id and secret as separate parameters."),
        new("  json: current.temp_c", "Pull one value out of a JSON response. Supports a.b and lists[0].c. Omit to use the whole body."),
        new("  json:", "Or a set of name: path entries, to take several values from one response — a latitude and longitude are useless apart."),
        new("  save_as: temp", "Name the result is stored under, so it becomes {http.temp}. Defaults to body → {http.body}."),
        new("  optional: true", "Treat a path that is not in the response as empty rather than a failure. For APIs where an empty answer is normal; pair with require:."),
        new("  headers:", "Extra request headers as name: value entries, for an API expecting a particular client. Naming User-Agent replaces MeshRF's. Secrets belong in a credential, not here."),
        new("  timeout: 10s", "How long to wait. Default 10s, maximum 30s."),
        new("  body: '{\"q\":\"{args}\"}'", "Request body for POST/PUT. Placeholders are JSON-escaped, so a quote in a message cannot break the field."),
        new("  content_type: …", "Defaults to application/json."),
        new("require:", "Stop the sequence unless something holds. The only way to act on what an http: returned, since conditions are settled before any action runs."),
        new("  value: \"{http.n}\"", "The value under test. Required."),
        new("  above: 30", "One comparison per require:. Also: below, at_least, at_most, between: [a, b], equals, not_equals, contains, matches, is_empty, not_empty."),
        new("  within: 30mi", "The value is a \"lat,lon\" no further than this from your home location. For an API that returns everything and leaves the filtering to you."),
        new("  ignore_case: false", "Text comparisons ignore case unless you turn this off."),
        new("waypoint:", "Drop a marker on the map. Takes the indented keys below."),
        new("  lat: \"{http.lat}\"", "Latitude, usually from an http: result. Or lat: home to use this node's home location, with no lon: needed."),
        new("  lon: \"{http.lon}\"", "Longitude."),
        new("  name: \"Lightning\"", "Marker label."),
        new("  description: \"…\"", "Longer text shown when the marker is opened."),
        new("  icon: ⛈", "Emoji shown on the map."),
        new("  radius: 30mi", "Geofence radius. Accepts mi, km, m; a bare number means metres."),
        new("  expires: 1h", "How long it lasts. Without one it stays on everyone's map until cleared by hand, and the editor warns."),
        new("  notify_on_enter: true", "Alert receivers crossing into the fence. Needs a radius. Also notify_on_exit."),
        new("  to: \"{from.id}\"", "Address the marker to one node instead of broadcasting it. It still travels under the primary's key — the address saves everyone else drawing it rather than keeping it from them."),
        new("  channel: LongFast", "Channel to broadcast on. Use one of to:/channel:, not both. Defaults to the primary, and channel: {primary} says so out loud."),
        new("  lock_to_me: false", "Let others edit the marker. On by default, so a script's markers cannot be rewritten."),
        new("  hops: 2", "Hop limit for the marker, 0-7, instead of the app-wide setting. A marker only means something to nodes near enough to act on it, so it is often worth fewer hops than the setting."),
        new("react: 👍", "Emoji tapback on the triggering message. Takes placeholders like reply: does, so react: \"{hops|keycap}\" tapbacks the hop count."),
        new("position: true", "Send this node's position."),
        new("nodeinfo: true", "Send this node's name, hardware and public key."),
        new("traceroute: true", "Request the route to the triggering node."),
        new("when:", "Optional on any action: run this one only while a test holds, and carry on with the rest either way. Takes the same value:/comparison keys as require:, so two replies with opposite when: entries is how a script chooses between them."),
        new("delay: 30s", "Wait before the next action. Maximum 1h."),
        new("log: \"text\"", "Write a line to the MeshRF log. Transmits nothing — useful while testing."),
        new("ring: default", "Sound the ringtone on this machine. Transmits nothing. Use 'default' for the ringtone configured in settings, or give RTTTL to play something else. Indent tune:/volume: entries to set both; volume is 0-100 and defaults to the configured one. Never sounds while the ringtone mode is Off."),
    ];

    public IReadOnlyList<HelpRow> Placeholders { get; } =
        ScriptPlaceholders.All.Select(p => new HelpRow($"{{{p.Token}}}", p.Description)).ToList();

    public IReadOnlyList<HelpRow> Filters { get; } =
        ScriptFilters.All.Select(f => new HelpRow($"{{value|{f.Name}}}", f.Description)).ToList();

    public IReadOnlyList<HelpRow> Limits { get; } = BuildLimits();

    private static IReadOnlyList<HelpRow> BuildLimits()
    {
        var d = new ScriptLimits();
        return
        [
            new("cooldown: 60s", $"Minimum gap between firings. Default {Describe(d.Cooldown)}."),
            new("per_node: true", $"Apply the cooldown separately per sending node, so one chatty node cannot mute the script for everyone. Default {(d.PerNode ? "true" : "false")}."),
            new("max_per_hour: 6", $"Hard ceiling on firings per rolling hour. Default {d.MaxPerHour}."),
            new("(global budget)", "30 transmissions per hour across every script together. Not settable from a script file — it is the ceiling a mistaken pattern cannot raise."),
        ];
    }

    public string HttpExample =>
        """
        enabled: true
        alias: Answer !wx with the temperature

        trigger:
          - command: wx

        action:
          - http:
              url: "https://api.example.com/v1/current?q={args}"
              credential: weather
              json: current.temp_c
              save_as: temp
          - reply: "{args}: {http.temp}°C"
        """;

    public IReadOnlyList<HelpRow> Http { get; } =
    [
        new("Two steps, not one", "http: fetches and stores; reply:/send: says it. That is what lets a script shape the answer, call more than one endpoint, or send the result somewhere other than back to the asker."),
        new("{http.<name>}", "The stored result. {http.status} always holds the response code."),
        new("If the fetch fails", "The rest of the script is skipped and the reason is logged. Broadcasting \"It's °C in London\" would be worse than saying nothing."),
        new("Credentials", "Stored under the Credentials button, protected at rest. A script names one; it can never read the value, so it can never broadcast it. Keys are never written to the log."),
        new("Dry run", "Still performs GET — seeing the real answer is most of what dry run is for, and a read changes nothing. POST and PUT are skipped, since those alter state on somebody else's server."),
        new("Responses", "Capped at 64 KB, stripped of control characters, collapsed onto one line, then clamped to the payload size. A response is somebody else's data about to go out on a shared channel."),
        new("Airtime", "A fetch transmits nothing, so it does not count against the global budget. The reply that follows it does."),
    ];

    public string SyncExample =>
        """
        enabled: true
        alias: Watch Duty fires

        sync:
          every: 15m
          url: "https://api.watchduty.org/api/v1/geo_events/?geo_event_types=*"
          headers:
            Accept: "application/json, text/plain, */*"

          items: ""            # the response is the array itself
          id: id               # identity, so a resend replaces
          active: is_active    # what "still here" means
          lat: lat
          lon: lng
          within: 30mi

          require:             # and only records that pass this
            - value: "{item.data.is_prescribed}"
              not_equals: true

          watch:               # resend only when one of these moves
            - data.acreage
            - data.containment

          waypoint:
            name: "Fire: {item.name}"
            description: "{item.data.acreage} acres"
            icon: "🔥"
            radius: 10mi
            channel: "{primary}"   # or a name, or to: for one node
        """;

    public IReadOnlyList<HelpRow> Sync { get; } =
    [
        new("every: 15m", "How often the feed is re-read. Minimum 1m. It reads once as soon as it is enabled, rather than waiting an interval."),
        new("url:, headers:", "As for http:, including credential: — the same request machinery."),
        new("items: results", "Path to the array of records. Leave empty when the response is the array itself."),
        new("id: id", "Path to each record's identity. This is what makes a resend replace a marker instead of adding another, so it must be stable for the record's life."),
        new("active: is_active", "Path to the flag saying a record is still live. One that goes false is retired exactly like one that stops being returned. Omit if every record returned counts."),
        new("lat: / lon:", "Paths to the position within each record."),
        new("within: 30mi", "Only mirror records this close to home. Omit to mirror the lot."),
        new("require:", "Tests a record has to pass to be worth a marker, written like a script's require: — a value: over {item.*} and one comparison. Give a list for more than one; all have to hold. This is how a feed that files two kinds of thing under one type gets narrowed to the one that means something on a map."),
        new("  what fails", "Failing counts as gone, not as unseen, so a record that stops qualifying has its marker retired like one the feed has dropped, and one that starts qualifying is placed."),
        new("watch: [a, b]", "Paths whose changes are worth resending for. With the wrong fields in it, a feed that restamps every record rebroadcasts everything on every poll."),
        new("watch: []", "Says the records never change — a lightning strike, not a fire. A marker is placed once and retired once, with nothing in between, and never refreshed. Different from leaving watch: out, which is only an omission and is warned about."),
        new("waypoint:", "The marker. Same keys as a script's, minus lat/lon — those come from the paths above — and its name and description are templates over {item.*}."),
        new("  expires:", "Usually omitted. A mirrored marker is retired when its record goes, not on a clock, so it does not need one."),
        new("  channel: Fires", "Which channel the markers go out on. Defaults to the primary, and channel: {primary} says so out loud. A feed worth its own channel keeps a mesh's shared one clear."),
        new("  to: \"!a1b2c3d4\"", "Or address them to one node. A literal id only — a feed places its markers unprompted, so there is no message for a placeholder to come from."),
        new("  lock_to_me:", "Off by default here, unlike a script's waypoint. These are placed unattended and may outlive this node's interest, so whoever receives one should be able to clear it."),
        new("  hops: 2", "Hop limit for the markers, 0-7, instead of the app-wide setting. A feed places far more frames than a script does, so the hops it does not need are the ones worth not spending."),
    ];

    public IReadOnlyList<HelpRow> SyncNotes { get; } =
    [
        new("Why not a script", "A record leaving a feed is not an event — nothing happens when something stops being in a list. Only something holding the previous list can notice, which is why this has its own engine rather than being a trigger."),
        new("What it sends", "A marker for each record it has not seen, a resend for one whose watched fields changed, and an expiry in the past for one that has gone. There is no delete on the wire; a past expiry is how a waypoint is retired."),
        new("Keeping one alive", "If the marker has an expires:, one still present is resent at half that age so it does not lapse while its record is live. watch: [] turns that off, since something that cannot change is meant to lapse."),
        new("After a restart", "The first poll re-places what is still there, over the top of itself, because a marker's id comes from the record's id rather than being random. Nothing accumulates."),
        new("Airtime", "A feed sends only what actually changed, so it is not charged against the script budget. watch: is what keeps that true."),
        new("Dry run", "Applies here too: every place, update and removal is logged and nothing is transmitted."),
    ];

    public IReadOnlyList<HelpRow> Running { get; } =
    [
        new("Run scripts", "Master switch, off by default. Until it is on, nothing fires however many scripts are enabled."),
        new("Dry run", "Everything is evaluated and logged, but nothing is transmitted. Limits are still consumed, so the log shows exactly what would have happened."),
        new("The log", "Every firing, every skipped action and every refusal is written to the main window's log, prefixed \"scripts:\"."),
        new("Reloading", "Saving, enabling, reordering or deleting a script reloads the engine straight away. Reloading also clears every cooldown, so an edited script can be tested at once."),
        new("every: on startup", "A scheduled trigger first fires one interval after scripts load, never the instant they do — otherwise every enabled beacon would transmit at once."),
        new("Scheduled triggers", "A timer event has no sender, so conditions asking about one (scope, from, snr_above, hops_below, favorite, has_key) never hold, and reply:/react: are skipped. Only between: is useful on a schedule."),
        new("new_node: timing", "A first sighting arrives before the node's name does. A greeting using {from.long} wants a delay: of 30s or so in front of it."),
    ];

    private static string Describe(TimeSpan span) =>
        span.TotalHours >= 1 ? $"{span.TotalHours:0.#}h"
        : span.TotalMinutes >= 1 ? $"{span.TotalMinutes:0.#}m"
        : $"{span.TotalSeconds:0.#}s";

    public IReadOnlyList<HelpRow> Values { get; } =
    [
        new("30s  5m  4h  1d", "Durations. A bare number means seconds."),
        new("30mi  50km  500m", "Distances. A bare number means metres."),
        new("\"08:00\"", "Times are 24-hour and local. Always quote them."),
        new("!a1b2c3d4", "Node ids, as shown in the Nodes table."),
        new("mode: single", "What a re-trigger does while a delay: is in flight. Also: restart, queued."),
        new("alias: My script", "Name shown in the list. Defaults to the file name."),
    ];

    public IReadOnlyList<HelpRow> EditorTips { get; } =
    [
        new("Enter", "Keeps your indentation, indents after a 'key:', and starts the next '- ' in a list."),
        new("Tab / Shift+Tab", "Indents or outdents by two spaces. YAML forbids tab characters, so pasted tabs are converted."),
        new("Suggestions", "A list of what this node already knows appears while you type a to:, from:, not_from:, channel: or credential: value — your configured channels, the nodes you have heard, and your stored credentials. Ctrl+Space asks for it, Enter or Tab accepts, Esc closes."),
        new("Node names", "A node id goes in quoted, since a bare !a1b2c3d4 opens a YAML tag, and the node's name is written in after it as a comment — eight hex digits say nothing about whose radio they are six months later."),
        new("Save", "Blocked while the script has errors — an unparseable file would be a script that silently never runs."),
        new("Click a problem", "Selects the line it came from."),
        new("Warnings", "Do not block saving. They flag things that parse but probably are not what you meant."),
        new("Samples", "samples/scripts in the MeshRF repository holds working starting points — a signal report, a ChatGPT bridge, a lightning waypoint and a wildfire waypoint. All ship disabled."),
    ];
}
