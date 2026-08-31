# Sample scripts

Working starting points for MeshRF's automation scripts. All of them are written
into your scripts folder the first time MeshRF runs, so there is nothing to copy
— open one, fill in the credential it names, and turn it on.

```
%APPDATA%\MeshRF\scripts          Windows
~/.config/MeshRF/scripts          Linux
~/Library/Application Support/MeshRF/scripts    macOS
```

The Scripts window's **Open folder** button takes you there, and **Reload**
picks up anything dropped in. Every sample ships `enabled: false`, so nothing
starts transmitting because it was installed.

Installation happens once. A folder that already has scripts in it is left
alone, and a sample you delete stays deleted — copy it back from here if you
want it again.

| Script | Needs | What it does |
| --- | --- | --- |
| [ping.yaml](ping.yaml) | nothing | Answers `!ping` with a signal report. |
| [quick-ping.yaml](quick-ping.yaml) | nothing | Adds a **Ping** button to the Quick send bar that sends `!ping` wherever you point it. |
| [test-hops.yaml](test-hops.yaml) | a channel named Test | Reacts to anything saying "test" there with a keycap emoji for the hop count. |
| [sos.yaml](sos.yaml) | one node id | Relays `!sos` to an operator by DM, with a marker at the sender's last position, wherever the call came from. |
| [ask-chatgpt.yaml](ask-chatgpt.yaml) | OpenAI key | Answers `!ask <question>` from the chat completions API. |
| [weather.yaml](weather.yaml) | OpenWeather key | Answers `!wx` with a report for where the sender is, or for a postcode or place they name. |
| [lightning-sync.yaml](lightning-sync.yaml) | Xweather id + secret, on a paid plan | Mirrors recent nearby strikes as point markers, retiring each as it ages out. |
| [wildfire-sync.yaml](wildfire-sync.yaml) | nothing | Mirrors *every* nearby Watch Duty fire, keeping markers in step as they change and retiring them when they go out. |

The last two are **feed syncs** rather than a script: instead of answering an
event, each keeps a set of waypoints in step with a list. It places a marker for
each record it has not seen, resends one whose watched fields changed, and
retires one that has gone. That last part is why it cannot be a script — a
record leaving a feed is not an event anything can trigger on. What it has
placed is remembered between runs, so restarting MeshRF does not re-broadcast
markers that are already out there.

A sync also says whether its records can change: the fire sync watches acreage
and containment, while the lightning sync declares `watch: []` — a strike is a
moment, so its marker is placed once and never resent.

Read a sync's header before enabling it: how many markers it can place depends
on how busy the feed is, and a storm makes far more lightning than a fire
season makes fires.

Xweather's free tier will not sustain the lightning sync. It bills in accesses
rather than requests and a lightning request costs about ten of them, so the
free 15,000 are gone in under a week at the five-minute poll the sample ships
with. It is enough to confirm the script works, not to leave running — and when
the allowance goes, the map cannot tell you, because no strikes returned looks
exactly like no strikes in the sky.

## Answering somewhere else

`reply:` answers in the conversation the trigger arrived on, which is right for
a signal report and wrong for anything that has to reach a particular person or
a particular channel. `send:` is the form that names its own destination:

```yaml
- send:
    to: "{from.id}"       # one node — a literal !a1b2c3d4 works too
    text: "…"
    reply_link: true      # still threaded under the message that triggered it
```

```yaml
- send:
    channel: Alerts       # one channel, whatever the trigger arrived on
    text: "…"
```

Use one of `to:` or `channel:`, never both. Neither means the primary, and
`channel: "{primary}"` says so out loud — which is the only way to name the
primary on a mesh running a default preset, since that channel has no name of
its own. The braces are what keep it unambiguous: a bare word in a `channel:`
is always a channel name, even if that name is "primary".

Waypoints take the same pair, in a script and in a sync alike, so a feed can go
on its own channel or to one node instead of onto everyone's map:

```yaml
  waypoint:
    name: "Fire: {item.name}"
    channel: Alerts       # or "{primary}", or to: "!a1b2c3d4" for one node
```

A marker addressed to a node still travels under the primary channel's key — the
address only says who it is for, so it saves everyone else drawing it rather
than keeping it from them. And a `channel:` naming something that does not exist
falls back to the primary and says so in the log, which is worth reading for
once after you change one.

[sos.yaml](sos.yaml) is the sample that does all three at once.

You do not have to look any of it up. Typing a `to:`, `from:`, `not_from:`,
`channel:` or `credential:` value in the Scripts editor drops down what this
node already knows — your configured channels, the nodes you have heard, and
your stored credentials. Ctrl+Space asks for the list, Enter or Tab accepts it,
Esc closes it. A node id is inserted quoted, and its name goes in after it as a
comment, because `!a1b2c3d4` tells you nothing about whose radio it is when you
come back to the script six months later.

Start with `ping.yaml`. It needs no account and no network, so if it answers,
the engine is armed and working and anything that goes wrong afterwards is the
script or the API rather than the setup.

## Running a script yourself

Every other trigger here waits for something to arrive. `quick_send:` does not:
it adds a button to the Quick send bar, beside the built-in ones, and the script
runs when you press it.

```yaml
trigger:
  - quick_send: Ping      # the label on the button
    to: ask               # asks where to send, like the buttons beside it
```

`to:` also takes a channel name or a node id, which makes it a button that goes
straight there without asking. Leave it out and it asks, because a button that
transmits somewhere its label never named is the one worth not having.

A press is not something that arrived, so it has a destination but no sender.
`scope:` and `channel:` read the destination and work as usual, while the
conditions that ask who sent it — `from:`, `snr_above:`, `hops_below:`,
`favorite:`, `has_key:` — can never hold, and a script using one will never fire
from a button. Inside the actions, a `send:` naming neither `to:` nor `channel:`
goes wherever the button was pointed.

Two scripts may name the same button. One button appears, and pressing it runs
both.

[quick-ping.yaml](quick-ping.yaml) is the sample, and the other half of
`ping.yaml`: one sends the `!ping`, the other answers it.

Worth being clear about who answers, though: `!ping` is a MeshRF convention
rather than a Meshtastic one. Stock firmware does not know what it means and
ignores it, as does any node whose owner has not enabled a script that answers
it. The node that replies is one running `ping.yaml` — and that sample is
`scope: direct`, so it answers direct messages and not channels. Point the
button at that node to get an answer; point it at a channel and the message
goes out and is simply read by people, which is still enough to see the button
work.

## How far a message travels

A `send:` or a `waypoint:` goes out at the hop limit under My Node, the same one
the compose box uses. Where a particular message should not, `hops:` gives it
its own, 0 to 7:

```yaml
- send:
    to: "{from.id}"
    text: "…"
    hops: 0               # direct neighbours only, never relayed
```

`hops: 0` is the cheapest thing a script can transmit: no node repeats it, so it
costs one airtime slot rather than one per relay in range. That is the right
answer for a signal report, an acknowledgement, or anything else that only means
something to whoever can already hear you — and the slider under My Node cannot
ask for it, since it starts at 1. Raise it above the setting only for a message
that genuinely has to cross the mesh; a relayed frame is spent from everyone's
airtime, not yours.

The same key works on a script's `waypoint:` and on a sync's, where it matters
more — a feed places far more frames than a script does, and a marker usually
only means something to nodes near enough to act on it.

## Before you enable anything

**Turn on Dry run.** Scripts are evaluated and logged in full but nothing is
transmitted, so you can watch a script fire, see the request it made and the
values it read, and correct it without putting a single frame on the air. It is
the difference between debugging a script and debugging a script in public.

Dry run still performs `GET` requests — a read changes nothing, and seeing the
real answer is the point — but skips `POST` and `PUT`.

## Working out an API's JSON paths

The paths in these samples are the fiddliest thing to get right, and the fastest
way to get them right is to look:

1. Delete the `json:` block from the `http:` action.
2. With Dry run on, trigger the script.
3. The log prints the whole response as `{http.body}`.
4. Read the field names off it, and put the paths back.

`json:` takes a single dotted path, or a set of `name: path` entries to pull
several values out of one response — which is what you want when the values
belong together, like a latitude and a longitude.

## Credentials

Keys never live in a script file. A script names a credential; the value is
stored under the Scripts window's **Credentials** button, protected at rest
alongside the MQTT password, and is never readable as a placeholder or written
to the log — so a script cannot broadcast its own key.

An API issuing an id *and* a secret is one credential, not two: fill in the
second parameter and value on the same entry.

## Airtime

These samples are throttled harder than they strictly need to be, because a
script that answers too eagerly is antisocial rather than merely noisy — the
channel is shared with everyone in range.

Beyond each script's own `cooldown` and `max_per_hour`, the engine applies a
global budget of 30 transmissions an hour across every script together, which a
script file cannot raise. Scripts also never answer your own node or one you
have ignored, and a message a script sent can never trigger another script.

Press **Help** in the Scripts window for the full vocabulary.
