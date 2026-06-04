# MeshtasticRF

Windows-native [Meshtastic](https://meshtastic.org/) **receiver** that uses an
SDR ([HackRF One](https://greatscottgadgets.com/hackrf/) or an RTL-SDR dongle)
as the radio instead of a LoRa modem chip. It demodulates LoRa chirps in
software (a port of
[`gr-lora_sdr`](https://github.com/tapparelj/gr-lora_sdr)), reassembles
Meshtastic frames, decrypts channels, and parses the mesh protobufs — all on
the host CPU.

> **Status:** receive-only. The full RX chain (SDR → DSP → LoRa demod →
> Meshtastic frame decode → channel decrypt → protobuf parse → UI) is working.
> **Transmit is not implemented** — this is a listen-only client.

## Features

- **Selectable SDR backend**: HackRF One or RTL-SDR, with auto-detection. The
  active device is chosen in the toolbar and can be switched while the receiver
  is stopped. Backends are loaded at runtime (no build-time dependency) and a
  synthetic source is used when no hardware is present.
- **Software LoRa demodulation** from raw SDR IQ (CSS / chirp-chat),
  configurable spreading factor, bandwidth, and coding rate via the standard
  Meshtastic LoRa presets.
- **Live spectrum + waterfall** with auto-levels, Turbo/Inferno colormaps, and a
  frozen per-packet spectrogram snapshot of the most recently detected frame.
- **Meshtastic frame decoding**: header parse, AES-CTR channel decryption
  (default-key family discovery included), and protobuf parsing.
- **Channels** with PSK management and per-channel message history (SQLite).
- **Direct messages**: double-click a node to open a conversation tab.
- **Node database** with positions, signal stats, and telemetry.
- **Telemetry**: device metrics (battery, voltage, channel/air utilization,
  uptime) and **environment metrics** (temperature, humidity, barometric
  pressure, gas resistance, IAQ), shown per node in its conversation tab.
- **Map view**: slippy OpenStreetMap tile map with node markers and a
  right-click-to-set home location.
- **X25519 key management** for PKI (key generation + public-key derivation).

## Architecture

```
+-----------------------------+
|  MeshtasticRF.App (WPF)     |   .NET 8, MVVM (CommunityToolkit.Mvvm)
+--------------+--------------+
               | P/Invoke (C ABI)
+--------------v--------------+
|  MeshtasticRF.Native (DLL)  |   C++20
|  - HAL (HackRF, RTL-SDR)    |
|  - DSP / LoRa demod         |   (port of gr-lora_sdr)
|  - Spectrum / waterfall     |
+-----------------------------+

Managed side (C#):
  MeshtasticRF.Core   - P/Invoke bindings, channel/node/message stores (SQLite),
                        Meshtastic frame decoder, AES-CTR + X25519 crypto
  MeshtasticRF.App    - WPF UI, view models, map, waterfall
```

## Building

Prerequisites:

- Visual Studio 2022 (MSVC v143) with the **Desktop development with C++** and
  **.NET desktop development** workloads
- CMake ≥ 3.25
- .NET 8 SDK
- [vcpkg](https://github.com/microsoft/vcpkg) (manifest mode — see `vcpkg.json`)
- An SDR with the WinUSB driver installed via [Zadig](https://zadig.akeo.ie/):
  a HackRF One, or an RTL-SDR dongle (`rtlsdr.dll`/`librtlsdr.dll` is loaded at
  runtime — e.g. from [PothosSDR](https://github.com/pothosware/PothosSDR) or
  SDRangel — and discovered on the module/known SDK paths).

```powershell
# Configure + build the native core (RelWithDebInfo recommended; Debug C++ is
# too slow for 2.4 MS/s streaming).
cmake --preset windows-x64
cmake --build build/windows-x64 --config RelWithDebInfo -j

# Build the managed app (copies the native DLL alongside it).
dotnet build app/MeshtasticRF.App/MeshtasticRF.App.csproj -c Debug
```

VS Code tasks are provided for **Build Native**, **Deploy Native DLL**,
**Build App**, **Build All**, and **Run App**.

## Testing

```powershell
# Native (GoogleTest)
ctest --test-dir build/windows-x64 --output-on-failure -C RelWithDebInfo

# Managed (xUnit)
dotnet test tests/managed/MeshtasticRF.Tests.csproj
```

## Layout

| Path | Purpose |
| ---- | ------- |
| `native/core/` | C++20 core: HAL, DSP, LoRa demod, spectrum |
| `native/bridge/` | C ABI surface (`extern "C"`) consumed by P/Invoke |
| `app/MeshtasticRF.App/` | WPF .NET 8 desktop UI |
| `app/MeshtasticRF.Core/` | Managed bindings, decoder, crypto, SQLite stores |
| `tests/native/` | GoogleTest unit tests |
| `tests/managed/` | xUnit unit tests |
| `third_party/hackrf/` | Vendored HackRF runtime DLLs |

## License

This project is licensed under **GPL-3.0-or-later** (see [LICENSE](LICENSE)).
The license is dictated by ports of / references to:

- [`gr-lora_sdr`](https://github.com/tapparelj/gr-lora_sdr) (DSP) — GPL-3.0
- [`meshtastic/protobufs`](https://github.com/meshtastic/protobufs) — GPL-3.0
- [`meshtastic/firmware`](https://github.com/meshtastic/firmware) (decode/crypto reference) — GPL-3.0

## Disclaimer

This is a receive-only client and does not transmit. It is an independent
project and is not affiliated with or endorsed by the Meshtastic project.
