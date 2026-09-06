// SPDX-License-Identifier: GPL-3.0-or-later
//
// C ABI consumed by the managed P/Invoke layer in app/MeshRF.Core.
// Keep this header valid C — no C++ types — so other languages can also bind.

#pragma once

#include "mrf/Export.h"

#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

typedef struct mrf_core_t mrf_core_t;

// Lifecycle ---------------------------------------------------------------
MRF_API mrf_core_t* MRF_CALL mrf_core_create(void);
MRF_API void        MRF_CALL mrf_core_destroy(mrf_core_t* core);

// Control -----------------------------------------------------------------
// preset corresponds to mrf::modem::Preset (enum order in Preset.h).
MRF_API int  MRF_CALL mrf_core_start_rx(mrf_core_t* core,
                                        int32_t preset,
                                        uint64_t center_freq_hz);

// Start RX with explicit modem parameters instead of a preset.
// sf = spreading factor (5..12), bw_hz = bandwidth in Hz (e.g. 250000),
// cr = coding rate denominator (5..8 → 4/N). Other params use Meshtastic defaults.
MRF_API int  MRF_CALL mrf_core_start_rx_params(mrf_core_t* core,
                                               uint8_t sf,
                                               uint32_t bw_hz,
                                               uint8_t cr,
                                               uint64_t center_freq_hz);

// One listener for mrf_core_start_rx_multi: a preset (mrf::modem::Preset
// ordinal), or explicit parameters when sf is non-zero, on a frequency.
typedef struct mrf_rx_listener_t {
    int32_t  preset;
    uint32_t sf;        // 0 = take the preset; else 7..12, with bw_hz and cr
    uint32_t bw_hz;
    uint32_t cr;        // 5..8 for 4/N
    uint64_t center_freq_hz;
} mrf_rx_listener_t;

// Start RX on several listeners off one capture centred on device_center_hz.
// The radio runs at a rate wide enough for every listener, and each
// listener's channel is mixed down, decimated and demodulated on a worker
// of its own. Listener 0 is the primary: the packet spectrogram follows its
// frames, and it is what a hardware modem receives, since an SX1262 takes
// one channel at a time and refuses more. Events carry the listener index;
// see mrf_core_pull_event_ex. Returns 0 on success, -1 on null, -2 when the
// core refused (no device, no supported rate covers the set, a hardware
// modem asked for more than one), -3 on parameters out of range.
MRF_API int MRF_CALL mrf_core_start_rx_multi(mrf_core_t* core,
                                             const mrf_rx_listener_t* listeners,
                                             uint32_t count,
                                             uint64_t device_center_hz);

MRF_API void MRF_CALL mrf_core_stop(mrf_core_t* core);
MRF_API int  MRF_CALL mrf_core_is_running(const mrf_core_t* core);

// Radio backend selection. `kind` mirrors mrf::hal::DeviceKind:
//   0 = legacy Auto (disabled), 1 = HackRF, 2 = RTL-SDR, 3 = None,
//   4 = SX1262 USB stick (transmit only; invalid as an RX selection).
// mrf_core_set_device is a back-compat alias for mrf_core_set_rx_device.
// Reopens the selected device immediately when RX is stopped so the names/status
// reflect the choices. Returns 0 on success, -1 if RX is running, -2 on null.
MRF_API int32_t MRF_CALL mrf_core_set_device(mrf_core_t* core, int32_t kind);
MRF_API int32_t MRF_CALL mrf_core_set_rx_device(mrf_core_t* core, int32_t kind);
MRF_API int32_t MRF_CALL mrf_core_set_tx_device(mrf_core_t* core, int32_t kind);

// The RX backend that actually opened (may differ from the requested kind).
MRF_API int32_t MRF_CALL mrf_core_get_device_kind(const mrf_core_t* core);
MRF_API int32_t MRF_CALL mrf_core_get_rx_device_kind(const mrf_core_t* core);

// The TX backend that actually opened/selected.
MRF_API int32_t MRF_CALL mrf_core_get_tx_device_kind(const mrf_core_t* core);

// 1 if the given backend's runtime library can be loaded (selectable), else 0.
MRF_API int32_t MRF_CALL mrf_core_device_available(const mrf_core_t* core,
                                                   int32_t kind);

// --- SX1262 packet transmitter -------------------------------------------
// Which CH341+SX126x stick is attached, mirroring mrf::hal::Sx126xBoard:
//   0 = MeshStick (bare SX1262, 22 dBm), 1 = MeshToad V3 (+PA, 30 dBm),
//   2 = Unspecified (the default: the transmitter will not open),
//   3 = uConsole AIO V2 (bare SX1262 on the host's SPI1, 22 dBm),
//   4 = Custom SPI (wiring and power model from mrf_set_custom_spi_board).
// The board also selects the transport: 0-2 are CH341 USB sticks, 3-4 are on
// the host's own SPI bus and are Linux-only.
// The two USB sticks are electrically identical, share USB IDs and report no
// distinguishing product string, so this is a user choice, not a detection.
// It only changes the power model — but getting it wrong misreports radiated
// power by ~8 dB, so nothing transmits until a real board is selected.
// Returns 0 on success, -1 on an unknown board, -2 on null.
MRF_API int32_t MRF_CALL mrf_core_set_sx1262_board(mrf_core_t* core, int32_t board);
MRF_API int32_t MRF_CALL mrf_core_get_sx1262_board(const mrf_core_t* core);

// Declares where a Custom SPI board's radio is wired and what its front end
// does, for board 4 above. Process-global, not per-core: it describes the
// machine. Line numbers are GPIO chip offsets (BCM numbers on a Raspberry Pi),
// -1 for absent; cs = -1 uses the SPI controller's own chip select. Power is
// in dBm at the antenna port, with pa_gain_db the difference between that and
// what is programmed into the chip. Returns 0 on success, -1 on failure.
MRF_API int32_t MRF_CALL mrf_set_custom_spi_board(
    const char* spidev, const char* gpiochip, int32_t speed_hz,
    int32_t cs, int32_t busy, int32_t reset, int32_t dio1, int32_t rxen,
    int32_t has_rxen, int32_t dio2_as_rf_switch, int32_t dio3_tcxo,
    int32_t tcxo_voltage, int32_t max_chip_dbm, int32_t pa_gain_db,
    int32_t min_out_dbm, int32_t max_out_dbm);

// Which stick to use when several are attached, by EEPROM serial — the only
// thing that distinguishes them, since they share VID/PID and report no
// product string. Empty or NULL takes the first that answers. Ignored while RX
// is running. mrf_core_list_sx1262_serials fills `buf` with the attached
// serials separated by '\n' and returns the byte count written (excluding the
// NUL); while a radio is open it reports only that radio's serial, because
// enumeration has to claim each device in turn.
MRF_API int32_t  MRF_CALL mrf_core_set_sx1262_serial(mrf_core_t* core, const char* serial);
MRF_API uint32_t MRF_CALL mrf_core_get_sx1262_serial(const mrf_core_t* core,
                                                     char* buf, uint32_t capacity);
MRF_API uint32_t MRF_CALL mrf_core_list_sx1262_serials(const mrf_core_t* core,
                                                       char* buf, uint32_t capacity);

// Transmit power at the antenna port, in dBm, for the SX1262 path. Clamped to
// the selected board's range. The HackRF path is unaffected and keeps using
// the txvga_gain_db argument to mrf_core_transmit.
MRF_API void    MRF_CALL mrf_core_set_tx_power_dbm(mrf_core_t* core, int32_t dbm);
MRF_API int32_t MRF_CALL mrf_core_get_tx_power_dbm(const mrf_core_t* core);

// Selectable dBm range for the currently selected board. Either pointer may
// be NULL. Valid before any device is connected.
MRF_API void MRF_CALL mrf_core_tx_power_range(const mrf_core_t* core,
                                              int32_t* min_dbm,
                                              int32_t* max_dbm);

// The band the operator declared by selecting a region, in Hz. SX1262
// transmits outside it are refused, because a stick's front end serves one
// band and cannot be identified over SPI. Receive is never restricted, and the
// HackRF path ignores this entirely. Both zero means undeclared, leaving only
// the SX1262's own 150-960 MHz range enforced. Reversed edges are normalized.
MRF_API void MRF_CALL mrf_core_set_tx_band_limits(mrf_core_t* core,
                                                  uint64_t min_hz,
                                                  uint64_t max_hz);
// Either pointer may be NULL.
MRF_API void MRF_CALL mrf_core_get_tx_band_limits(const mrf_core_t* core,
                                                  uint64_t* min_hz,
                                                  uint64_t* max_hz);

// IQ capture: dump the decimated modem-input stream (interleaved float32
// I/Q, ".cf32") to `path`. Safe to toggle while RX runs. Capped to ~60 s.
MRF_API int  MRF_CALL mrf_core_start_capture(mrf_core_t* core, const char* path);
MRF_API void MRF_CALL mrf_core_stop_capture(mrf_core_t* core);
MRF_API int  MRF_CALL mrf_core_is_capturing(const mrf_core_t* core);

// Live gain control. Clamped to HackRF ranges (lna 0..40, vga 0..62).
MRF_API void MRF_CALL mrf_core_set_gains(mrf_core_t* core,
                                         uint8_t lna_db,
                                         uint8_t vga_db,
                                         int32_t amp_enable);

// Device-specific option (RTL-SDR: "adc_agc", "bias_tee"; value 0/1). Unknown
// keys are ignored. Cached across stop/start.
MRF_API void MRF_CALL mrf_core_set_device_option(mrf_core_t* core,
                                                 const char* key,
                                                 int32_t value);

// Diagnostics ------------------------------------------------------------
typedef struct mrf_signal_stats_t {
    float rssi_dbfs;
    float peak_dbfs;
    float dc_re;
    float dc_im;
    uint64_t total_samples;
} mrf_signal_stats_t;

MRF_API void     MRF_CALL mrf_core_get_signal_stats(const mrf_core_t* core,
                                                    mrf_signal_stats_t* out);

// Listeners in the current configuration (1 after a single-listener start),
// and the signal level inside one listener's channel after its channel
// filter, where mrf_core_get_signal_stats covers the whole capture. An index
// past the count fills a silent snapshot.
MRF_API uint32_t MRF_CALL mrf_core_listener_count(const mrf_core_t* core);
MRF_API void     MRF_CALL mrf_core_get_listener_signal_stats(const mrf_core_t* core,
                                                             uint32_t index,
                                                             mrf_signal_stats_t* out);

// Returns the spectrum size in bins (0 if RX is not running). The caller
// should pre-allocate a float[spectrum_size] buffer.
MRF_API uint32_t MRF_CALL mrf_core_spectrum_size(const mrf_core_t* core);

// Returns the device sample rate in Hz (0 if RX is not running). This is the
// full span of the spectrum/waterfall; DC (bin n/2) maps to the tuned center
// frequency.
MRF_API uint32_t MRF_CALL mrf_core_sample_rate_hz(const mrf_core_t* core);

// Returns the centre frequency of the displayed spectrum in Hz: what the
// radio is tuned to, which is the channel after a single-listener start and
// the device centre after a multi-listener one. Use this for frequency-axis
// labels. 0 if not running.
MRF_API uint64_t MRF_CALL mrf_core_spectrum_center_hz(const mrf_core_t* core);

// Copies the latest dBFS spectrum frame into out. Returns the number of bins
// copied (0 if no frame is available or capacity is insufficient). Bins are
// FFT-shifted: index 0 is the most negative frequency, index n/2 is DC.
MRF_API uint32_t MRF_CALL mrf_core_pull_spectrum(const mrf_core_t* core,
                                                 float* out_dbfs,
                                                 uint32_t capacity);

// Returns a monotonic count of spectrum FFT frames produced since the pipeline
// started. One frame corresponds to spectrum_size() received samples, so the
// delta between two calls is proportional to elapsed received-signal time. Use
// this to advance the waterfall in step with received data instead of the UI
// refresh rate. 0 if not running.
MRF_API uint64_t MRF_CALL mrf_core_spectrum_frame_count(const mrf_core_t* core);

// Returns the effective frame rate in Hz of the history stream used by
// mrf_core_pull_spectrum_frames()/mrf_core_spectrum_frame_count(). This can
// be lower than sample_rate_hz/spectrum_size when native history decimation
// is active at high sample rates. 0 if not running.
MRF_API uint32_t MRF_CALL mrf_core_spectrum_history_frame_rate_hz(
    const mrf_core_t* core);

// Extract up to max_count individual spectrum frames from the rolling history,
// starting after after_frame_idx. Fills out_frames row-major with max_count rows
// of spectrum_size() floats each. Returns the number of frames actually extracted.
// out_frames must hold at least max_count * spectrum_size() floats.
MRF_API uint32_t MRF_CALL mrf_core_pull_spectrum_frames(
    const mrf_core_t* core,
    uint64_t after_frame_idx,
    uint32_t max_count,
    float* out_frames,
    uint32_t out_frames_len);

// Computes a high-time-resolution spectrogram of the most recent ~150 ms of
// modem-rate IQ, cropped to the LoRa channel. Fills out_dbfs row-major as
// n_time rows of n_freq dBFS values (low->high freq left->right). out_dbfs
// must hold at least n_time*n_freq floats. Returns the number of rows written
// (n_time) or 0 if not enough IQ history is available.
MRF_API uint32_t MRF_CALL mrf_core_pull_packet_spectrogram(const mrf_core_t* core,
                                                           float* out_dbfs,
                                                           uint32_t n_time,
                                                           uint32_t n_freq);

// Copies a NUL-terminated UTF-8 device name into `buf` (up to `capacity`
// bytes including the NUL). Returns the number of bytes that would be needed
// (excluding NUL). If buf is null or capacity is 0, no copy is made.
MRF_API uint32_t MRF_CALL mrf_core_get_device_name(const mrf_core_t* core,
                                                   char* buf,
                                                   uint32_t capacity);
MRF_API uint32_t MRF_CALL mrf_core_get_tx_device_name(const mrf_core_t* core,
                                                      char* buf,
                                                      uint32_t capacity);

// Diagnostic string from the most recent device-open attempt.
MRF_API uint32_t MRF_CALL mrf_core_get_device_status(const mrf_core_t* core,
                                                     char* buf,
                                                     uint32_t capacity);

// Pop the next pending demodulator event into `buf` (UTF-8, NUL-terminated).
// Returns the number of bytes written (excluding NUL), or 0 if no event is
// queued. Oversized events are truncated and still popped so one large log
// line cannot block the event queue.
MRF_API uint32_t MRF_CALL mrf_core_pull_event(mrf_core_t* core,
                                              char* buf,
                                              uint32_t capacity);

// As mrf_core_pull_event, also reporting which listener the line is about:
// its index in the table given to mrf_core_start_rx_multi (0 after a
// single-listener start), or -1 for the receiver as a whole. May be NULL.
MRF_API uint32_t MRF_CALL mrf_core_pull_event_ex(mrf_core_t* core,
                                                 char* buf,
                                                 uint32_t capacity,
                                                 int32_t* listener_index);

// Transmit ----------------------------------------------------------------
// 1 if the selected TX radio backend can transmit (HackRF only), else 0.
MRF_API int32_t MRF_CALL mrf_core_can_transmit(const mrf_core_t* core);

// Modulates `payload` (the fully framed/encrypted on-air bytes produced by the
// managed layer) into a LoRa burst for `preset` and transmits it centered on
// `center_freq_hz`. HackRF only; if TX shares the RX HackRF, RX is paused for
// the burst and resumed afterwards. Separate RX/TX devices can run full duplex.
// `txvga_gain_db` is the HackRF TX VGA gain (0..47).
// Blocks until the burst has been streamed. Returns 1 on success, 0 if the
// device cannot transmit, the payload is empty, or modulation failed.
MRF_API int32_t MRF_CALL mrf_core_transmit(mrf_core_t* core,
                                           int32_t preset,
                                           uint64_t center_freq_hz,
                                           const uint8_t* payload,
                                           uint32_t payload_len,
                                           uint8_t txvga_gain_db,
                                           int32_t amp_enable);

// Transmit with explicit modem parameters instead of a preset.
MRF_API int32_t MRF_CALL mrf_core_transmit_params(mrf_core_t* core,
                                                  uint8_t sf,
                                                  uint32_t bw_hz,
                                                  uint8_t cr,
                                                  uint64_t center_freq_hz,
                                                  const uint8_t* payload,
                                                  uint32_t payload_len,
                                                  uint8_t txvga_gain_db,
                                                  int32_t amp_enable);

// Returns the bridge ABI version. Bumped on breaking change.
MRF_API uint32_t MRF_CALL mrf_abi_version(void);

#ifdef __cplusplus
} // extern "C"
#endif
