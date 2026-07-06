# MeshRF

MeshRF is a Windows-native Meshtastic SDR transceiver.

Instead of using a LoRa modem chip, MeshRF uses an SDR (HackRF One or RTL-SDR)
and performs LoRa demodulation/modulation in software on the host CPU. It
decodes Meshtastic frames, decrypts channel payloads, parses protobufs, and
provides a desktop UI for channels, nodes, map, telemetry, and messaging.

Current release line: **v1.0.3**

<img width="1386" height="993" alt="image" src="https://github.com/user-attachments/assets/108e4d84-f767-44c0-a9bb-2750e67c33d7" />


## Status

- Receive path is operational end-to-end: SDR IQ -> DSP -> LoRa demod ->
  Meshtastic frame decode -> decrypt -> parse -> UI.
- Transmit path is operational for channel broadcast, direct messages, and
  control/management packets.
- The app is actively maintained with frequent updates focused on map scale,
  messaging UX, telemetry/routing controls, and observability.

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
- Waypoint send/receive support.
- Traceroute and request-position / node-info exchanges.
- Optional `ok_to_mqtt` transmit flag.

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

- WPF desktop app (.NET 8) with MVVM architecture.
- Channel/DM tabs with persisted history.
- RTTTL notification controls (including per-channel mute options).
- Improved auto-scroll and large-node-count map performance tuning.
- Payload recording and JSON-focused logging improvements for analysis/replay.

## Architecture

```text
MeshRF.App   (.NET 8 WPF)
  - UI, map, waterfall, view models, app settings
  - P/Invoke into native bridge DLL

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

- Windows 10/11 x64.
- Visual Studio 2022 with:
  - Desktop development with C++
  - .NET desktop development
- CMake 3.25+
- .NET 8 SDK
- SDR hardware and drivers (typically via Zadig/WinUSB as needed):
  - HackRF One, or
  - RTL-SDR dongle

Notes:

- Native SDR dependencies are built from source submodules (`third_party/hackrf`
  and `third_party/rtlsdr`) during native build; resulting runtime DLLs are
  copied next to app outputs.
- On Windows, CMake auto-provisions a repo-local `.vcpkg` and installs required
  native packages (currently `libusb` and `pthreads`) when needed.
- Meshtastic protobuf schemas are linked via git submodule at
  `third_party/meshtastic_protobufs`.
- Default development flow expects native `RelWithDebInfo` for practical SDR
  throughput.

### Submodules

If you use VS Code `Build Native` / `Build & Run` tasks, submodules are
initialized automatically by the `Init Submodules` task.

For CLI/manual workflows, initialize linked dependencies after clone:

```powershell
git submodule update --init --recursive
```

Update Meshtastic protobuf schemas later:

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
cmake --preset windows-x64
cmake --build build/windows-x64 --config RelWithDebInfo -j
```

### Managed App

```powershell
dotnet build app/MeshRF.App/MeshRF.App.csproj -c Debug --nologo
```

The app project copies `MeshRF.Native.dll` and required SDR runtime DLLs from
`build/windows-x64/bin/<NativeConfig>/` into the managed output folder after
build.

## Run

```powershell
dotnet run --project app/MeshRF.App/MeshRF.App.csproj -c Debug --no-build
```

VS Code tasks are already included for configure/build/test/run workflows.

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

`scripts/build-release.ps1` builds a self-contained, single-file `win-x64`
release and packages it as:

`dist/MeshRF-v<version>-win-x64.zip`

Usage:

```powershell
# Use VersionPrefix from Directory.Build.props
pwsh scripts/build-release.ps1

# Override version and optionally tag
pwsh scripts/build-release.ps1 -Version 0.8.2
pwsh scripts/build-release.ps1 -Version 0.8.2 -Tag
```

The release bundle includes:

- Published MeshRF app
- Native bridge/runtime DLLs
- `LICENSE`
- `README.md`

## Repository Layout

| Path | Purpose |
| --- | --- |
| `app/MeshRF.App/` | WPF desktop application |
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
- [meshtastic/protobufs](https://github.com/meshtastic/protobufs)
- [meshtastic/firmware](https://github.com/meshtastic/firmware)

## Disclaimer

MeshRF is an independent project and is not affiliated with or endorsed by the
Meshtastic project.
