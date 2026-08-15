# MeshRF

MeshRF is a cross-platform Meshtastic SDR transceiver for Windows, Linux and
macOS.

Instead of using a LoRa modem chip, MeshRF uses an SDR (HackRF One or RTL-SDR)
and performs LoRa demodulation/modulation in software on the host CPU. It
decodes Meshtastic frames, decrypts channel payloads, parses protobufs, and
provides a desktop UI for channels, nodes, map, telemetry, and messaging.

Current release line: **v2.0.3**

### Two apps, for one release

| App | Version | Platforms | Status |
| --- | --- | --- | --- |
| `MeshRF.App.Avalonia` | 2.0.3 | Windows, Linux, macOS | The app MeshRF ships |
| `MeshRF.App` (WPF) | 1.0.9 | Windows only | Final release — no longer maintained |

Both read the same `settings.json` and the same SQLite databases, so you can
move between them. After 2.0.0 / 1.0.9, only the Avalonia app is developed, and
everything below describes it.

<img width="1547" height="990" alt="image" src="https://github.com/user-attachments/assets/ba593234-2b41-4b29-85e2-f85aba94e5fe" />


## Status

- Receive path is operational end-to-end: SDR IQ -> DSP -> LoRa demod ->
  Meshtastic frame decode -> decrypt -> parse -> UI.
- Transmit path is operational for channel broadcast, direct messages, and
  control/management packets.
- The app is actively maintained with frequent updates focused on map scale,
  messaging UX, telemetry/routing controls, and observability.
- Windows, Linux and macOS all build the native core and the app from source.
  Windows is the most exercised; Linux and macOS builds are produced by CI and
  have had far less time on real radio hardware.

## Key Capabilities

### Radio and Signal Processing

- Runtime-selectable SDR backend: HackRF One or RTL-SDR.
- Independent RX and TX device selection.
- Software LoRa demod/mod with Meshtastic-oriented preset support.
- Optional receive conditioning features (including DC blocking).
- Live spectrum and waterfall with packet-linked snapshot support.

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
- Home location from manual map selection or USB serial GPS source.
- Filtering for nodes, telemetry presence, ignore state, and position-history
  presence.
- Configurable map node label modes.

### UI and Workflow

- Cross-platform Avalonia desktop app (.NET 8, Windows/Linux/macOS) with MVVM
  architecture. The original Windows-only WPF app is being retired; both ship
  side by side for one release, after which Avalonia is the only app.
- Channel/DM tabs with persisted history.
- RTTTL notification controls (including per-channel mute options).
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

- **Triggers**: `command`, `text` (regex), `new_node`, `reaction`, `every`, `at`.
- **Conditions**: `scope`, `channel`, `from` / `not_from`, `snr_above`,
  `hops_below`, `between`, `favorite`, `has_key`.
- **Actions**: `reply`, `send`, `react`, `position`, `nodeinfo`, `traceroute`,
  `http`, `delay`, `log`.

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

MeshRF.App   (.NET 8 WPF — Windows only, being retired)
  - Same, for the legacy Windows build

MeshRF.Core  (.NET 8 class library)
  - Native interop bindings
  - Meshtastic frame decode/encode helpers
  - Crypto helpers and key handling
  - SQLite stores (channels, nodes, messages, waypoints)

MeshRF.Native (C++20, built with CMake)
  - SDR HAL (HackRF, RTL-SDR)
  - DSP + LoRa modem pipeline
  - Spectrum/waterfall and native packet plumbing
```

## Requirements

Common to every platform:

- CMake 3.25+
- .NET 8 SDK
- SDR hardware: HackRF One, or an RTL-SDR dongle

**Windows 10/11 x64**

- Visual Studio 2022 or newer with "Desktop development with C++" and ".NET
  desktop development". The `windows-x64` preset pins no generator, so CMake
  uses the newest Visual Studio it finds.
- SDR drivers as needed (typically via Zadig/WinUSB).

**Linux x64**

```bash
sudo apt-get install -y ninja-build cmake libhackrf-dev librtlsdr-dev \
                        libusb-1.0-0-dev libudev-dev \
                        autoconf autoconf-archive automake libtool
```

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
  `third_party/meshtastic_protobufs`, tracking the `games` branch of
  [Ixitxachitl/meshtastic-protobufs](https://github.com/Ixitxachitl/meshtastic-protobufs/tree/games)
  — a fork of upstream [meshtastic/protobufs](https://github.com/meshtastic/protobufs)
  whose only addition is a set of game/leaderboard messages. Every other field
  MeshRF uses (geofence, ATAK, etc.) is upstream Meshtastic.
- Default development flow expects native `RelWithDebInfo` for practical SDR
  throughput.

### Submodules

If you use VS Code `Build Native` / `Build & Run` tasks, submodules are
initialized automatically by the `Init Submodules` task.

For CLI/manual workflows, initialize linked dependencies after clone:

```powershell
git submodule update --init --recursive
```

Update Meshtastic protobuf schemas later (pulls the latest commit on the
fork's `games` branch, per `.gitmodules`):

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

VS Code tasks are included for configure/build/test/run workflows; the primary
Build/Run buttons target the Avalonia app, with the WPF app under a "legacy"
group.

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
| Linux | `MeshRF-v<version>-linux-x64.tar.gz` |
| macOS | `MeshRF-v<version>-osx-arm64.zip` (or `-osx-x64`) |

```powershell
# The Avalonia app, versioned from its own project
pwsh scripts/build-release.ps1

# The legacy WPF app, or both (Windows only)
pwsh scripts/build-release.ps1 -App Wpf
pwsh scripts/build-release.ps1 -App Both

# Override the version, and optionally tag the repo version
pwsh scripts/build-release.ps1 -Version 2.0.1
pwsh scripts/build-release.ps1 -Tag
```

Each app is versioned from its own project file, falling back to
`Directory.Build.props`, so `-App Both` produces `MeshRF-v2.0.0-*` and
`MeshRF-wpf-v1.0.9-*` in one run.

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
| `app/MeshRF.App/` | Legacy WPF desktop application (Windows only, being retired) |
| `app/MeshRF.Core/` | Managed protocol/interop/storage library |
| `native/core/` | C++ SDR/DSP/LoRa core |
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
- [meshtastic/protobufs](https://github.com/meshtastic/protobufs) (via the
  [Ixitxachitl/meshtastic-protobufs](https://github.com/Ixitxachitl/meshtastic-protobufs/tree/games)
  fork linked as `third_party/meshtastic_protobufs`)
- [meshtastic/firmware](https://github.com/meshtastic/firmware)

## Disclaimer

MeshRF is an independent project and is not affiliated with or endorsed by the
Meshtastic project.
