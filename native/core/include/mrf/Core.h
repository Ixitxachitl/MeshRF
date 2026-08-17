// SPDX-License-Identifier: GPL-3.0-or-later
#pragma once

#include "mrf/Export.h"
#include "mrf/hal/PacketRadio.h"
#include "mrf/hal/RadioDevice.h"
#include "mrf/modem/Preset.h"
#include "mrf/router/FloodingRouter.h"

#include <cstddef>
#include <memory>
#include <span>
#include <string>
#include <string_view>
#include <vector>

namespace mrf {

// Lightweight POD mirroring dsp::SignalStats::Snapshot, exposed publicly so
// the C ABI / managed bindings can carry a stable shape.
struct CoreSignalStats {
    float rssi_dbfs;
    float peak_dbfs;
    float dc_re;
    float dc_im;
    std::uint64_t total_samples;
};

// Top-level facade tying HAL + modem + MAC + router together. The C ABI
// (native/bridge) wraps a single instance of this class.
class Core {
public:
    Core();
    ~Core();

    Core(const Core&) = delete;
    Core& operator=(const Core&) = delete;

    void start_rx(modem::Preset preset, std::uint64_t center_freq_hz);
    void start_rx(const modem::LoraParams& params, std::uint64_t center_freq_hz);
    void stop();

    // Transmit a single Meshtastic frame. `payload` is the on-air byte stream
    // (16-byte L1 header + encrypted Data payload) produced by the managed
    // MeshEncoder; this modulates it into a full LoRa frame (preamble + sync +
    // SFD + FEC + chirps), upsamples to the radio rate, offset-mixes, and keys
    // the transmitter. If TX uses the same HackRF that is currently receiving,
    // RX is paused for the burst and resumed afterward; when RX is on a
    // separate device, RX continues during TX. Blocks until the burst has been
    // streamed. Returns false if the selected TX device cannot transmit or the
    // payload is empty/invalid.
    //
    // When the TX device is the SX1262 stick, none of the software modulation
    // above happens: the payload is handed to the radio, which does preamble,
    // sync, FEC and chirping itself, at the power set by set_tx_power_dbm().
    // RX is never paused in that case — the stick is a separate USB device, so
    // the SDR keeps receiving (and will hear the burst) throughout.
    bool transmit(modem::Preset preset, std::uint64_t center_freq_hz,
                  std::span<const std::uint8_t> payload,
                  std::uint8_t txvga_gain_db = 30, bool amp_enable = false);
    bool transmit(const modem::LoraParams& params, std::uint64_t center_freq_hz,
                  std::span<const std::uint8_t> payload,
                  std::uint8_t txvga_gain_db = 30, bool amp_enable = false);

    // True if the selected TX radio backend can transmit (HackRF only).
    [[nodiscard]] bool can_transmit() const noexcept;

    // Select the RX radio backend used for the next start_rx. Reopens the device
    // immediately (so device_name()/device_status() reflect the choice) when
    // RX is not running. Returns false if RX is currently running (the caller
    // must stop first).
    bool set_rx_device(hal::DeviceKind kind);

    // Back-compat alias for set_rx_device().
    bool set_device(hal::DeviceKind kind) { return set_rx_device(kind); }

    // Select the TX radio backend. HackRF and the SX1262 USB stick can
    // transmit; RTL-SDR and Null cannot. Like RX selection, this is only
    // allowed while RX is stopped.
    bool set_tx_device(hal::DeviceKind kind);

    // Which CH341+SX126x stick is plugged in. The two supported boards are
    // electrically identical, enumerate with the same USB IDs and report no
    // distinguishing product string, so they cannot be told apart
    // automatically; this only changes the power model (the MeshToad's
    // external PA adds ~8 dB), never the wiring. Takes effect on the next
    // set_tx_device(Sx1262), and re-opens the device if one is already open.
    //
    // Defaults to Sx126xBoard::Unspecified, in which state the transmitter
    // refuses to open at all: a guess here is silently wrong in the dangerous
    // direction, since a MeshToad driven as a MeshStick radiates ~8 dB more
    // than the UI reports.
    void set_sx1262_board(hal::Sx126xBoard board);
    [[nodiscard]] hal::Sx126xBoard sx1262_board() const noexcept;

    // Which stick to use when several are attached, by EEPROM serial — the
    // only thing that distinguishes them. Empty takes the first that answers.
    // Ignored while RX is running; stop first to switch sticks.
    void set_sx1262_serial(std::string_view serial);
    [[nodiscard]] std::string sx1262_serial() const;

    // Serials of the attached sticks, for a device picker. While a radio is
    // open this returns just that radio's serial, because enumeration has to
    // claim each device in turn and cannot run against one already in use.
    [[nodiscard]] std::vector<std::string> list_sx1262_serials() const;

    // Transmit power at the antenna port, in dBm, for packet TX devices. The
    // backend subtracts any external PA gain and clamps to the board's range,
    // so this is the number the user actually radiates. Ignored by the HackRF
    // path, which uses the txvga_gain_db argument to transmit() instead.
    void set_tx_power_dbm(std::int8_t dbm);
    [[nodiscard]] std::int8_t tx_power_dbm() const noexcept;

    // Selectable antenna-port power range for the current SX1262 board, so
    // the UI can bound its control before a device is connected.
    void tx_power_range_dbm(std::int8_t& min_dbm, std::int8_t& max_dbm) const noexcept;

    // The band the operator declared by selecting a region, in Hz. Packet
    // transmits outside it are refused: a stick's front end is built for one
    // band, cannot be identified over SPI, and a band-limited PA driven far
    // off-band can be damaged rather than merely inefficient. Receive is never
    // restricted.
    //
    // Both zero (the default) means undeclared, leaving only the SX1262's own
    // 150-960 MHz range enforced. Callers that track a region should set this
    // whenever it changes. Ignored by the HackRF path, whose front end is
    // broadband by design.
    void set_tx_band_limits(std::uint64_t min_hz, std::uint64_t max_hz);
    void tx_band_limits(std::uint64_t& min_hz, std::uint64_t& max_hz) const noexcept;

    // The RX backend that actually opened (may differ from the requested kind
    // when Auto probes, or when the requested device was unavailable).
    [[nodiscard]] hal::DeviceKind rx_device_kind() const noexcept;

    // Back-compat alias for rx_device_kind().
    [[nodiscard]] hal::DeviceKind device_kind() const noexcept { return rx_device_kind(); }

    // The TX backend that actually opened/selected.
    [[nodiscard]] hal::DeviceKind tx_device_kind() const noexcept;

    // True if a backend's runtime library can be loaded (so the user could
    // select it). Auto and Null are always available; does not need hardware.
    [[nodiscard]] bool is_device_available(hal::DeviceKind kind) const noexcept;

    // Begin capturing the decimated modem-input IQ stream (interleaved
    // float32 I/Q, ".cf32") to `path`. Safe to call while RX is running.
    // Returns true if the file was opened. Overwrites any existing capture.
    bool start_capture(const char* path);
    // Stop and flush any in-progress capture. No-op if not capturing.
    void stop_capture();
    [[nodiscard]] bool is_capturing() const noexcept;

    // Live gain control. Values are clamped to HackRF ranges:
    //   lna_db: 0..40 step 8, vga_db: 0..62 step 2, amp: 0/1.
    // Safe to call before or after start_rx; takes effect immediately when
    // RX is running, and is remembered for the next start.
    void set_gains(std::uint8_t lna_db, std::uint8_t vga_db, bool amp);

    // Device-specific option setter for backends with controls that don't map
    // onto the HackRF gain model. Recognised keys (RTL-SDR): "adc_agc" and
    // "bias_tee" (value 0/1). Unknown keys are ignored. Cached across stop/start.
    void set_device_option(std::string_view key, int value);

    [[nodiscard]] bool is_running() const noexcept;

    // Returns the FFT/spectrum frame size used by the running pipeline. 0 if
    // not running.
    [[nodiscard]] std::size_t spectrum_size() const noexcept;

    // Device sample rate (Hz) of the running pipeline. This equals the full
    // span of the spectrum/waterfall (DC at the tuned center frequency). 0 if
    // not running.
    [[nodiscard]] std::uint32_t sample_rate_hz() const noexcept;

    // Actual center frequency of the displayed spectrum in Hz. Because the
    // radio is offset-tuned by kLoOffsetHz, this equals the user's channel
    // frequency plus that offset. 0 if not running.
    [[nodiscard]] std::uint64_t spectrum_center_hz() const noexcept;

    // Copy the latest dBFS spectrum into `out`. Returns true if a frame is
    // available and out.size() >= spectrum_size().
    bool latest_spectrum(std::span<float> out) const;

    // Monotonic count of spectrum FFT frames produced since the pipeline
    // started. One frame corresponds to spectrum_size() samples of received
    // signal, so the delta between two reads is proportional to the amount of
    // real signal time elapsed. Use this to advance the waterfall at a rate
    // tied to received data rather than the UI refresh rate. 0 if not running.
    [[nodiscard]] std::uint64_t spectrum_frame_count() const noexcept;

    // Effective frame rate (Hz) of the decimated spectrum-history stream used
    // by pull_spectrum_frames()/spectrum_frame_count(). This can be lower than
    // sample_rate_hz()/spectrum_size() when history decimation is enabled to
    // bound UI pull load at high device sample rates. 0 if not running.
    [[nodiscard]] std::uint32_t spectrum_history_frame_rate_hz() const noexcept;

    // Extract up to max_count individual spectrum frames from the rolling
    // history, starting after after_frame_idx. Fills out_frames row-major as
    // max_count rows of spectrum_size() dBFS values each. Returns the actual
    // count extracted (0 to max_count).
    [[nodiscard]] std::uint32_t pull_spectrum_frames(
        std::uint64_t after_frame_idx,
        std::uint32_t max_count,
        std::span<float> out_frames) const;

    // Compute a high-time-resolution spectrogram of the most recent ~150 ms of
    // modem-rate IQ, cropped to the LoRa channel. Fills `out` row-major as
    // n_time rows of n_freq dBFS values (low->high freq left->right, matching
    // the live waterfall). Returns the number of rows written (n_time) or 0 if
    // not enough IQ history is available. out.size() must be >= n_time*n_freq.
    std::uint32_t pull_packet_spectrogram(std::span<float> out,
                                          std::uint32_t n_time,
                                          std::uint32_t n_freq) const;

    [[nodiscard]] CoreSignalStats signal_stats() const noexcept;

    // Pop the next pending demodulator event (UTF-8) into `out`. Returns the
    // number of bytes copied (excluding any NUL), or 0 if no event is queued
    // or `out` is too small. Events are queued by the modem (e.g. "preamble
    // detected: ...") and consumed by the UI for display in the log.
    std::size_t pull_event(std::span<char> out) noexcept;

    // Human-readable name of the RX radio backend currently in use
    // (e.g. "HackRF One", or "(none)" if no RX device is selected/available).
    [[nodiscard]] const char* device_name() const noexcept;

    // Human-readable name of the TX radio backend currently selected.
    [[nodiscard]] const char* tx_device_name() const noexcept;

    // Diagnostic string from the most recent device-open attempt, e.g.
    // "HackRF open OK" or "libhackrf load failed: hackrf.dll not found".
    [[nodiscard]] const char* device_status() const noexcept;

private:
    struct Impl;
    std::unique_ptr<Impl> impl_;
};

} // namespace mrf
