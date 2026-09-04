# MeshRF

MeshRF is a cross-platform Meshtastic SDR transceiver for Windows, Linux and
macOS.

MeshRF's usual mode uses an SDR (HackRF One or RTL-SDR) with no LoRa modem chip
at all, performing LoRa demodulation and modulation in software on the host CPU.
It decodes Meshtastic frames, decrypts channel payloads, parses protobufs, and
provides a desktop UI for channels, nodes, map, telemetry, and messaging.

An SX1262 hardware modem can also be used for either direction, on its own or
alongside an SDR — over a CH341 USB stick on any platform, or on the host's own
SPI bus on Linux (uConsole AIO V2, Raspberry Pi HATs). See
[SX1262 hardware modems](#sx1262-hardware-modems).

Current release line: **v2.4.0**

<img width="1547" height="990" alt="image" src="https://github.com/user-attachments/assets/ba593234-2b41-4b29-85e2-f85aba94e5fe" />


## Status

- Receive path is operational end-to-end: SDR IQ -> DSP -> LoRa demod ->
  Meshtastic frame decode -> decrypt -> parse -> UI. An SX1262 modem can take
  the place of everything left of the frame decode, handing up finished frames
  instead of IQ.
- Transmit path is operational for channel broadcast, direct messages, and
  control/management packets, either modulated in software onto a HackRF or
  handed to an SX1262 modem.
- The app is actively maintained with frequent updates focused on map scale,
  messaging UX, telemetry/routing controls, and observability.
- Windows, Linux and macOS all build the native core and the app from source,
  x64 everywhere and arm64 on Linux and macOS. Windows is the most exercised;
  Linux and macOS builds are produced by CI and have had far less time on real
  radio hardware. The linux-arm64 artifact and the SPI radio path it exists for
  have not been run on hardware at all yet.

## Key Capabilities

### Radio and Signal Processing

- Runtime-selectable radio backend: HackRF One or RTL-SDR for receive, HackRF
  One for transmit, or an SX1262 hardware modem for either direction.
- Independent RX and TX device selection, so an SDR receiver can be paired with
  a hardware transmitter. See [SX1262 hardware modems](#sx1262-hardware-modems).
- Software LoRa demod/mod with Meshtastic-oriented preset support.
- Optional receive conditioning features (including DC blocking).
- Live spectrum and waterfall with packet-linked snapshot support (SDR receive
  only — a hardware modem produces no IQ).

### SX1262 hardware modems

MeshRF normally modulates and demodulates LoRa in software using an SDR. It can
instead hand framed bytes to an SX1262, which does preamble, sync, FEC and
chirping itself. The radio is selectable for **RX, TX or both**.

An SX1262 is reached over one of two buses, chosen by the board you pick:

| Bus | Boards | Platforms |
| --- | --- | --- |
| CH341 USB-SPI bridge | Elecrow MeshStick, NullHop/muzi MeshToad V3 | Windows, Linux, macOS |
| The host's own SPI bus | uConsole AIO V2, Raspberry Pi HATs | Linux only |

Everything above the PHY — decrypt, protobuf, routing, MQTT, the whole UI — is
identical either way, because both paths emit the same frame events the
software demodulator does.

The intended setup is **SDR receive + SX1262 transmit**. It keeps everything
that makes MeshRF worth using — the spectrum, waterfall, packet spectrogram and
IQ capture — while fixing the weak leg: HackRF TX is ~10 dBm of unfiltered
wideband output, where these modems put out 22 or 30 dBm through a matched
front end. Because the modem is a device of its own — a second USB device, or a
radio on the SPI bus while the SDR is on USB — **RX is never paused for a
burst**; the waterfall stays live throughout, and the receiver hears the
transmission.

#### USB sticks

**Receiving** on the stick as well makes MeshRF a complete node for someone who
owns a LoRa stick and no SDR — one stick serves both directions half-duplex,
the way real Meshtastic hardware does. The cost is everything an SDR gives you:
a hardware modem produces decoded frames, never IQ, so the spectrum, waterfall,
packet snapshot and IQ capture all go away and the display says so. In exchange
the RSSI and SNR are the radio's own measurements rather than estimates off an
IQ stream, sensitivity is ~20 dB better, and the CPU is idle. Every layer above
the PHY — decrypt, protobuf, routing, MQTT, the whole UI — is unchanged, because
the hardware path emits the same frame events the software demodulator does.

Supported boards, selected in the TX toolbar next to the power control:

| Board | Radio | Antenna-port power |
| --- | --- | --- |
| Elecrow MeshStick | bare SX1262 | -9 .. 22 dBm |
| NullHop / muzi MeshToad V3 | SX1262 + E22P-915M30S | -1 .. 30 dBm |

Both enumerate as `1a86:5512` with an identical pin map, and neither reports a
USB product string to tell them apart, so **the board picker starts empty and
nothing will transmit until you choose**. That gate exists because a wrong
guess is silent in the worse direction: a MeshToad driven as a MeshStick
radiates about 8 dB more than the UI reports, with no warning about the current
draw. The reverse — a MeshStick set to MeshToad — is harmless, because the
requested power is clamped to the SX1262's own +22 dBm ceiling either way. No
board selection can overdrive the radio; the risk is purely mislabelling.

The choice only selects the power model: the MeshToad's external PA adds
roughly 8 dB, so the dBm shown in the UI is what leaves the antenna, not what
is programmed into the chip. Above 22 dBm a MeshToad can draw ~900 mA, more
than a USB 2.0 port is obliged to supply; the UI warns, and a powered hub is
the fix.

Driver requirements:

- **Windows** — install the [WCH CH341PAR](https://www.wch-ic.com/downloads/CH341PAR_EXE.html)
  package. MeshRF loads `CH341DLLA64.DLL` at runtime, the same way it loads
  `hackrf.dll`, so it works against the driver binding meshtasticd users
  already have. No Zadig re-bind, and nothing to configure.
- **Linux / macOS** — libusb, the same path meshtasticd uses. On Linux the
  `ch341` kernel module has to be blacklisted or detached, and the device needs
  udev permissions.

Only one process can own a stick at a time, so MeshRF and a local `meshtasticd`
cannot share one. Selecting any other RX/TX device releases it immediately.

With more than one stick attached, a **Stick** picker appears offering each
one's EEPROM serial — the only thing that distinguishes them, since they all
share `1a86:5512` and report no product string. With a single stick the picker
stays hidden and the first device found is used.

#### SPI boards (Linux)

On a single-board computer the radio is usually soldered to the host's own SPI
bus rather than hanging off USB. MeshRF drives those through `/dev/spidevB.D`
and the GPIO character device — the same wiring meshtasticd uses, so a board
with a meshtasticd config already has its pin map written down.

| Board | Radio | Antenna-port power |
| --- | --- | --- |
| uConsole AIO V2 | bare SX1262 on SPI1 | -9 .. 22 dBm |
| Custom SPI board | whatever you declare | whatever you declare |

Only one preset ships, and deliberately: a pin map can be read off a config
file, but a **power model cannot**. Nothing on an SPI bus reports whether a
power amplifier sits after the chip, and meshtasticd's configs do not record
one either — they cap chip power instead. Assuming a board is bare when it has
an E22-style front end is wrong in the direction that over-radiates: the UI
would say 22 dBm while the antenna saw 30. The uConsole AIO V2 is listed
because it is genuinely a bare SX1262 with nothing after it. Every other board
goes through **Custom SPI board**, where the front end is yours to declare.

Requirements:

- SPI enabled (`dtparam=spi=on`, or `raspi-config`), so `/dev/spidev*` exists.
- Read/write on the spidev node and the GPIO chip — the `spi` and `gpio`
  groups on Raspberry Pi OS. No root needed.
- Nothing else holding the radio. `meshtasticd` claims the same GPIO lines, and
  MeshRF will say so by name rather than failing vaguely.

**uConsole AIO V2** additionally gates its peripherals behind GPIOs that are
off at boot: LoRa on 16, SDR on 7, GPS on 27, the internal USB hub on 23. Until
the AIO's own enable has been set, neither the radio nor the RTL-SDR exists as
far as MeshRF is concerned.

**Custom SPI board** takes its wiring and power model from `settings.json`
under `CustomSpi`. Line numbers are GPIO chip offsets, which on a Raspberry Pi
are the BCM numbers meshtasticd quotes. `Cs: -1` leaves chip select to the SPI
controller, which is the usual wiring; give it a line number only if your board
routed CS to an ordinary GPIO. `RxEn: -1` is right for any board whose DIO2
runs the RF switch.

```json
"CustomSpi": {
  "SpiDev": "spidev0.0",
  "GpioChip": "gpiochip0",
  "SpeedHz": 2000000,
  "Cs": -1, "Busy": 20, "Reset": 24, "Dio1": 16, "RxEn": 12,
  "Dio2AsRfSwitch": true, "Dio3Tcxo": true, "TcxoVoltage": 2,
  "MaxChipDbm": 22, "PaGainDb": 8, "MinOutDbm": -1, "MaxOutDbm": 30
}
```

Pin maps for common HATs, transcribed from `bin/config.d` in
meshtastic/firmware. **The power fields are not included** — fill those in from
your own module's datasheet, per the reasoning above.

| Board | SpiDev | Busy | Reset | Dio1 | RxEn |
| --- | --- | --- | --- | --- | --- |
| MeshAdv Mini E22-900M22S | `spidev0.0` | 20 | 24 | 16 | 12 |
| Nebra SX1262 Pi HAT | `spidev0.0` | 4 | 18 | 22 | 25 |
| PiTastic / ZebraHat 1W | `spidev0.0` | 27 | 17 | 22 | -1 |
| RAK6421 13300 (slot 1) | `spidev0.0` | 24 | 16 | 22 | -1 |

Boards with a separate **TXen** line (MeshAdv-Pi 900M30S, and others that
switch transmit and receive with two pins) are not supported: the driver drives
one RF-switch line, not two.

### Meshtastic Protocol Support

- Channel decode/decrypt with PSK handling.
- Channel and direct messaging workflows.
- PKC direct messaging with X25519 key exchange and AES-256-CCM message
  protection for DM payloads.
- Routing ACK/NACK-based delivery state.
- Reply-linked messages and per-message emoji reactions.
- Waypoint send/receive support, including circular and bounding-box
  geofences with enter/exit alerts.
- Traceroute and request-position / node-info exchanges.
- MQTT bridge with per-channel uplink/downlink, mirroring firmware's MQTT
  module: same default server/credentials/root topic
  (`mqtt.meshtastic.org` / `meshdev` / `large4cats` / `msh`), encrypted
  ServiceEnvelope publishing (both the channel-encrypted and, when disabled,
  the plaintext-decoded wire forms), self-originated packets uplinked the
  same as received ones, SNR-independent gating rules (`ok_to_mqtt`,
  default-server port suppression, PKI-aware). Off by default; configured
  from the MQTT toolbar button, with Uplink/Downlink toggles per channel in
  that channel's Settings. Optional periodic MapReport publishing (name,
  role, hardware, firmware version, region/preset, fuzzed location) to the
  broker's map topic, matching firmware's map-reporting feature. Optional
  parallel JSON publish/subscribe (firmware `json_enabled`): human-readable
  per-port JSON alongside every uplink, plus "sendtext"/"sendposition"
  remote-command downlink on a channel named "mqtt".
- Self-reported firmware version/edition (Identity settings) surfaced to MQTT
  map reports, defaulting to the same baseline as stock firmware
  (`2.8.0` / `VANILLA`).

### Nodes, Telemetry, and Mapping

- SQLite-backed channel, node, message, and waypoint persistence.
- Device metrics and environment metrics display.
- Channel utilization and TX airtime surfaced in the UI.
- OpenStreetMap-based map view with clustering and location history support.
- Home location from manual map selection or USB serial GPS source, with a
  smart-position filter so a receiver reporting every second only moves the
  marker when it has actually moved.
- Filtering for nodes, telemetry presence, ignore state, and position-history
  presence.
- Configurable map node label modes.
- Link profile between this station and any positioned node: terrain
  cross-section from Terrarium elevation tiles, first Fresnel zone, single
  knife-edge diffraction loss, and the LoRa link budget for the modem in use.
  Where the node is a direct neighbour the measured SNR is shown against the
  predicted one, so the gap is the clutter the terrain model does not carry.
- Path-loss calibration from that gap: a log-distance model fitted by least
  squares to every direct neighbour heard over the air, with the terrain loss
  to each one taken out first. The fitted exponent says how fast signal falls
  off at this site; applying it puts that clutter loss into every link
  prediction. Outliers are visible as residuals and can be dropped from the fit.
- Coverage ring over the map: a compass sweep of how far the station reaches in
  each direction, coloured by whether terrain cost that direction anything. Each
  bearing is walked outward to where contiguous coverage ends, judged against
  the range the same radio gets over open ground, and the fitted path loss is
  used in place of free space wherever a calibration has been applied.

### UI and Workflow

- Cross-platform Avalonia desktop app (.NET 8, Windows/Linux/macOS) with MVVM
  architecture.
- Channel/DM tabs with persisted history.
- RTTTL notification tones for messages, geofence crossings and alert bells,
  each with its own duration or Off, behind a **Notifications** button. Volume
  is shared; muting is available per channel and per conversation.
- Alert bell button beside the compose box. It shows a bell in the message and
  adds Meshtastic's `ASCII_BELL` on the way out, so a receiving node sounds its
  external notification. Incoming alerts are marked on the bubble, which is the
  only way to see one from a client that sends the character alone.
- Improved auto-scroll and large-node-count map performance tuning.
- Drag-to-reorder for secondary channel tabs and DM tabs.
- Emoji picker built from the colour emoji font's actual glyph coverage, so it
  offers every emoji the system can draw and nothing it can't.
- Raw decoded-packet JSON feed with export, for analysis and replay.

### Automation Scripts

MeshRF can answer messages and transmit on a schedule, driven by YAML scripts
in `%APPDATA%\MeshRF\scripts` (one file per script). The shape follows Home
Assistant's automations — a list of triggers, conditions that all have to hold,
and a sequence of actions — with a closed vocabulary and no expression
language.

```yaml
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
```

- **Triggers**: `command`, `text` (regex), `new_node`, `reaction`, `every`, `at`,
  `quick_send` (adds a named button to the Quick send bar and runs when pressed;
  its `to:` asks for a destination, or names a channel or node).
- **Conditions**: `scope`, `channel` / `not_channel`, `from` / `not_from`,
  `snr_above`, `hops_below`, `between`, `favorite`, `has_key`.
- **Actions**: `reply`, `send`, `react`, `position`, `nodeinfo`, `traceroute`,
  `http`, `waypoint`, `require`, `delay`, `log`, `ring`.
- **Reach**: `send:` and `waypoint:` take `hops:` (0-7) to override the app-wide
  hop limit for one message. `hops: 0` is never repeated by any node, so it
  costs one airtime slot rather than one per relay in range — the right answer
  for anything that only means something to whoever can already hear you.

A `waypoint:` action drops a marker, optionally with a geofence and enter/exit
alerts. A `require:` action stops the sequence unless a value holds — which is
how a script acts on what an `http:` call returned, since conditions are settled
before any action runs:

```yaml
trigger:
  - every: 10m

action:
  - http:
      url: "https://api.example.com/lightning?p={my.lat},{my.lon}&radius=30mi"
      credential: [api-id, api-secret]   # one name, or several
      optional: true                      # an empty answer is normal here
      json:                               # several values, one response
        lat: response[0].loc.lat
        lon: response[0].loc.long
  - require:
      value: "{http.lat}"
      not_empty: true
  - waypoint:
      lat: "{http.lat}"
      lon: "{http.lon}"
      name: "Lightning"
      radius: 30mi
      expires: 1h
      notify_on_enter: true
```

`{my.lat}` and `{my.lon}` carry this node's home location, so a script asking a
location-shaped question needs no coordinates pasted into it.

Working starting points live in [samples/scripts/](samples/scripts/) — a signal
report, a ChatGPT bridge, a lightning waypoint and a wildfire waypoint. All ship
disabled; copy one into the scripts folder, fill in the credential it names, and
turn it on.

A **feed sync** is the other half: instead of answering something that happened,
it keeps a set of waypoints in step with a REST feed. It polls, places a marker
for each record it has not seen, resends one whose watched fields changed, and
retires one that has gone — a record leaving a list is not an event, so only
something holding the previous list can notice it. `require:` narrows a feed to
the records worth a marker, and failing it counts as gone rather than as unseen,
so a record that stops qualifying clears itself off everyone's map:

```yaml
sync:
  every: 15m
  url: "https://api.watchduty.org/api/v1/geo_events/?geo_event_types=*"
  items: ""              # the response is the array itself
  id: id                 # identity, so a resend replaces
  active: is_active
  lat: lat
  lon: lng
  within: 30mi
  require:
    - value: "{item.data.is_prescribed}"
      not_equals: true   # a planned burn is not "fire near you"
  watch: [data.acreage, data.containment]
  waypoint:
    name: "Fire: {item.name}"
    icon: "🔥"
    radius: 10mi
```

A script can call a REST API and broadcast the answer. Fetching and sending are
two steps, so the result can be shaped into a sentence, combined from more than
one endpoint, or sent somewhere other than back to the asker:

```yaml
trigger:
  - command: wx

action:
  - http:
      url: "https://api.example.com/v1/current?q={args}"
      credential: weather      # names a key stored in the app, not here
      json: current.temp_c     # dotted path into the JSON response
      save_as: temp            # becomes {http.temp}
  - reply: "{args}: {http.temp}°C"
```

API keys are optional, and are stored under the Scripts window's **Credentials**
button rather than in the script — protected at rest, attachable as a bearer
token, a named header or a query parameter. A script names a credential and can
never read its value, so it cannot broadcast it, and keys are never written to
the log. Placeholders inside a `url:` are percent-encoded and inside a JSON
`body:` are JSON-escaped, so a received message cannot rewrite the request.
Responses are capped, flattened to one line and clamped to the payload size. A
failed fetch skips the rest of the script rather than broadcasting a
half-formed sentence. Dry run still performs `GET` (a read changes nothing) but
skips `POST`/`PUT`.
- **Scripts window** (the *Scripts* button): lists every script in execution
  order with an enable toggle, and an embedded editor that refuses to save a
  script it cannot parse, reporting the line and column and suggesting the key
  you probably meant. Help documents the full vocabulary.

Airtime is shared, so the safety rails are on by default and are not all
settable from a script file: scripts never answer your own node or an ignored
one, a message a script sent can never trigger another script, each script has
a cooldown and an hourly cap, and a global budget of 30 transmissions/hour
applies across every script together. The master switch is off until you turn
it on, and **Dry run** evaluates and logs everything without transmitting.

## Architecture

```text
MeshRF.App.Avalonia  (.NET 8 Avalonia — Windows/Linux/macOS)
  - UI, map, waterfall, view models, app settings
  - P/Invoke into native bridge library

MeshRF.Core  (.NET 8 class library)
  - Native interop bindings
  - Meshtastic frame decode/encode helpers
  - Crypto helpers and key handling
  - SQLite stores (channels, nodes, messages, waypoints)

MeshRF.Native (C++20, built with CMake)
  - SDR HAL (HackRF, RTL-SDR) — IQ in, IQ out
  - Packet-radio HAL (SX126x over CH341 USB-SPI or Linux spidev) — framed
    bytes, no IQ
  - DSP + LoRa modem pipeline
  - Spectrum/waterfall and native packet plumbing
```

## Requirements

Common to every platform:

- CMake 3.25+
- .NET 8 SDK
- Radio hardware: a HackRF One or RTL-SDR dongle, and/or an SX1262 modem
  (see [SX1262 hardware modems](#sx1262-hardware-modems)). A modem alone is enough
  for both directions; an SDR alone can receive, and needs a HackRF to transmit.

**Windows 10/11 x64**

- Visual Studio 2022 or newer with "Desktop development with C++" and ".NET
  desktop development". The `windows-x64` preset pins no generator, so CMake
  uses the newest Visual Studio it finds.
- SDR drivers as needed (typically via Zadig/WinUSB).
- For an SX1262 USB stick, the WCH CH341PAR driver package (not Zadig).

**Linux x64 / arm64**

```bash
sudo apt-get install -y ninja-build cmake libhackrf-dev librtlsdr-dev \
                        libusb-1.0-0-dev libudev-dev \
                        autoconf autoconf-archive automake libtool
```

arm64 additionally needs `VCPKG_FORCE_SYSTEM_BINARIES=1`, which the
`linux-arm64` preset sets for the configure step. Export it in your shell too
if you are bootstrapping vcpkg yourself: it ships no prebuilt tools for that
architecture and has to build its own with the system compiler.

**macOS (arm64 or x64)**

```bash
brew install ninja cmake hackrf librtlsdr libusb autoconf autoconf-archive automake libtool
```

Linux and macOS also need `VCPKG_ROOT` pointing at a vcpkg checkout.

Notes:

- On Windows, native SDR dependencies are built from source submodules
  (`third_party/hackrf`, `third_party/rtlsdr`) and the resulting runtime DLLs
  are copied next to app outputs. On Linux and macOS they come from system
  packages and are loaded at runtime via `dlopen`, so the submodules are not
  built there.
- On Windows, CMake auto-provisions a repo-local `.vcpkg` when no toolchain is
  supplied. It is cloned in full on purpose: `vcpkg.json` pins a
  `builtin-baseline` commit that a shallow clone cannot resolve.
- The autotools packages above are for vcpkg's own `libusb` port, which
  configures from source.
- Meshtastic protobuf schemas are linked via git submodule at
  `third_party/meshtastic_protobufs`, tracking `master` on upstream
  [meshtastic/protobufs](https://github.com/meshtastic/protobufs) with no
  local modifications. Every field MeshRF uses — geofence, ATAK and the rest —
  is official Meshtastic.
- Default development flow expects native `RelWithDebInfo` for practical SDR
  throughput.

### Submodules

If you use VS Code `Build Native` / `Build & Run` tasks, submodules are
initialized automatically by the `Init Submodules` task.

For CLI/manual workflows, initialize linked dependencies after clone:

```powershell
git submodule update --init --recursive
```

Update Meshtastic protobuf schemas later (pulls the latest commit on upstream
`master`, per `.gitmodules`):

```powershell
git submodule update --remote -- third_party/meshtastic_protobufs
```

## Build

### Quick Start (VS Code)

From a fresh clone, run task `Build & Run`.

This task chain will:

- initialize submodules,
- configure and build native components,
- deploy native bridge/runtime DLLs into app output,
- build managed app,
- run the app.

### Native (CMake)

```powershell
# Windows
cmake --preset windows-x64
cmake --build build/windows-x64 --config RelWithDebInfo -j
```

```bash
# Linux. Ninja is single-config, so the build type is set at configure time.
cmake --preset linux-x64 -D CMAKE_BUILD_TYPE=Release
cmake --build build/linux-x64 -j

# Linux arm64 (Raspberry Pi, uConsole) — native, not cross-compiled
cmake --preset linux-arm64 -D CMAKE_BUILD_TYPE=Release
cmake --build build/linux-arm64 -j

# macOS (use macos-x64 on Intel)
cmake --preset macos-arm64 -D CMAKE_BUILD_TYPE=Release
cmake --build build/macos-arm64 -j
```

### Managed App

```powershell
dotnet build app/MeshRF.App.Avalonia/MeshRF.App.Avalonia.csproj -c Debug --nologo
```

The app project copies the native bridge (`MeshRF.Native.dll`,
`libMeshRF.Native.so` or `libMeshRF.Native.dylib`) — and on Windows the SDR
runtime DLLs — from the platform's `build/<preset>/bin/` directory into the
managed output folder after build.

## Run

```powershell
dotnet run --project app/MeshRF.App.Avalonia/MeshRF.App.Avalonia.csproj -c Debug --no-build
```

VS Code tasks are included for configure/build/test/run workflows.

## Testing

### Native Tests

```powershell
ctest --test-dir build/windows-x64 --output-on-failure -C RelWithDebInfo
```

### Managed Tests

```powershell
dotnet test tests/managed/MeshRF.Tests.csproj --nologo
```

## Release Packaging

`scripts/build-release.ps1` runs under PowerShell 7 on all three platforms. It
detects the host, picks the matching CMake preset, RID and archive format, and
builds a self-contained single-file release into `dist/`:

| Host | Artifact |
| --- | --- |
| Windows | `MeshRF-v<version>-win-x64.zip` |
| Linux x64 | `MeshRF-v<version>-linux-x64.tar.gz` |
| Linux arm64 | `MeshRF-v<version>-linux-arm64.tar.gz` |
| macOS | `MeshRF-v<version>-osx-arm64.zip` (or `-osx-x64`) |

```powershell
# Package for the host platform
pwsh scripts/build-release.ps1

# Override the version, and optionally tag it
pwsh scripts/build-release.ps1 -Version 2.0.1
pwsh scripts/build-release.ps1 -Tag
```

The version comes from the app project's `VersionPrefix`, falling back to
`Directory.Build.props`.

The bundle includes the published app, the native bridge (plus the SDR runtime
DLLs on Windows), `LICENSE`, `README.md`, and on Linux a `.desktop` entry and
icon.

Native libraries cannot be cross-compiled, so each platform's artifact must be
built on that platform — or in CI. `.github/workflows/release.yml` builds all
three on their own runners and drafts a GitHub release when a `v*` tag is
pushed; it checks the tag against `VersionPrefix` first and fails loudly on a
mismatch.

## Repository Layout

| Path | Purpose |
| --- | --- |
| `app/MeshRF.App.Avalonia/` | Cross-platform desktop application (Windows/Linux/macOS) |
| `app/MeshRF.Core/` | Managed protocol/interop/storage library |
| `native/core/` | C++ SDR/DSP/LoRa core, plus the SX126x packet radio |
| `native/bridge/` | C ABI bridge DLL for P/Invoke |
| `tests/managed/` | Managed unit tests |
| `tests/native/` | Native unit tests |
| `scripts/` | Utility and release scripts |
| `third_party/meshtastic_protobufs/` | Meshtastic protobuf schema submodule |
| `third_party/hackrf/` | HackRF source submodule (built during native build) |
| `third_party/rtlsdr/` | RTL-SDR source submodule (built during native build) |

## Licensing

This project is licensed under **GPL-3.0-or-later**. See [LICENSE](LICENSE).

Upstream references influencing licensing and implementation include:

- [gr-lora_sdr](https://github.com/tapparelj/gr-lora_sdr)
- [meshtastic/protobufs](https://github.com/meshtastic/protobufs) (linked as
  `third_party/meshtastic_protobufs`)
- [meshtastic/firmware](https://github.com/meshtastic/firmware)
- [MeshLab RF](https://github.com/HarukiToreda/MeshLab-RF) (MIT), whose
  propagation model the link profile follows

## Disclaimer

MeshRF is an independent project and is not affiliated with or endorsed by the
Meshtastic project.
