// SPDX-License-Identifier: GPL-3.0-or-later
//
// LoRa / "ChirpChat" receiver front-end. Ports the dechirp + FFT + sliding
// preamble detector from SDRangel's `ChirpChatDemodSink` (plugins/channelrx/
// demodchirpchat) in simplified form. This stage produces *symbols* and a
// preamble-detected event; header / payload decode is handled separately.
//
// Algorithm summary, matching the reference receiver:
//
//   1. Reference upchirp c[n] = exp(j*pi*(n*(n-1)/N - n)) for n in [0, N),
//      with N = 2^SF. Downchirp = conj(upchirp).
//   2. Receive at chip rate (= bandwidth_hz). Group every N samples into a
//      candidate symbol, multiply by the downchirp, FFT, take |X|^2, peak
//      bin = symbol value (0..N-1).
//   3. Preamble detector: a LoRa preamble is K identical upchirps (Meshtastic
//      defaults to K=16 over the air, we accept >= 6). We track the peak
//      bin of every candidate symbol and look for >= kPreambleConfirm
//      consecutive symbols with peak bin within +/- 1 of each other.
//   4. The locked peak bin yields the carrier-frequency offset:
//        cfo_hz = peak_bin * bandwidth_hz / N   (mod bandwidth_hz, signed)
//
// The detector slides one sample at a time so it doesn't need the caller to
// know the symbol boundary in advance.

#pragma once

#include "mrf/dsp/Fft.h"
#include "mrf/modem/Preset.h"

#include <complex>
#include <cstdint>
#include <deque>
#include <functional>
#include <span>
#include <string>
#include <vector>

namespace mrf::modem {

struct PreambleEvent {
    int          symbol_value;    // locked peak bin (0..N-1)
    float        cfo_hz;          // signed carrier-frequency offset
    float        peak_db;         // dB above the mean of the FFT magnitude
    int          confirm_count;   // number of consecutive matching symbols
    std::uint64_t sample_index;   // index of the first sample of the lock
};

// Emitted for every symbol captured after a preamble lock, until tracking
// drops (currently after kTrackingSymbols symbols or a quiet period). This
// is the raw demapped symbol value (peak FFT bin) — header/payload decode
// is a later stage.
struct SymbolEvent {
    int          index_in_frame; // 0-based symbol index after sync
    int          symbol_value;   // 0..N-1
    float        peak_db;
    std::uint64_t sample_index;
};

// Emitted once per frame after the 8 header symbols have been captured and
// run through Gray-decode + diagonal-deinterleave + Hamming(8,4). The five
// decoded nibbles of the LoRa explicit header are exposed as a 5-byte field
// (top nibble of each = upper, bottom nibble = lower) so the UI can show
// sensible values even if the timing isn't perfect yet.
struct HeaderEvent {
    std::uint8_t  payload_length;   // bytes
    std::uint8_t  coding_rate;      // 1..4 (CR=4/5..4/8)
    bool          has_crc;
    std::uint8_t  raw_nibbles[10];  // 8 deinterleaved codewords -> up to 10 nibbles
    std::size_t   nibble_count;
    bool          parity_ok;        // header CRC-5 (not yet validated, always false for now)
    std::uint64_t sample_index;
    std::uint32_t payload_symbol_count;

    // -- Diagnostics ----------------------------------------------------
    // The 8 raw header symbol bins as captured (post integer-CFO correction,
    // 0..N-1) plus the recovered sync parameters, so a failed header can be
    // analyzed without live re-runs.
    std::uint16_t raw_symbols[8];
    int           cfo_int;
    int           sto_int;
    int           chosen_delta;     // header-lock delta that was applied (0 if none)
    int           chosen_start;     // anchor start offset that was applied (0 if none)

    // Frame-sync internals captured at QuarterDown, for diagnosing why a
    // header fails to lock: coarse preamble bin, fractional CFO/STO estimates,
    // the raw SFD down-chirp dechirp bin, and absorbed extra preamble chirps.
    int           k_hat;            // coarse preamble bin (majority vote)
    float         cfo_frac;         // fractional CFO estimate (cycles/sample)
    float         sto_frac;         // fractional STO estimate (chips), post-refine
    int           down_val;         // SFD down-chirp dechirp bin (=2*cfo_int)
    int           add_upchirps;     // extra preamble up-chirps absorbed in NetId1
    int           net_id0;          // first sync-word symbol bin (post cfo-frac)
    int           net_id1;          // second sync-word symbol bin (post cfo-frac)
};

// Emitted once per frame after the payload symbols are demapped, deinterleaved,
// Hamming-decoded and dewhitened. `bytes` holds up to `length` data bytes
// followed by 2 CRC-16 bytes when `has_crc`. `crc_ok` is set if the trailing
// CRC matches the recomputed CRC over `bytes[0..length)`.
struct PayloadEvent {
    std::uint8_t  bytes[260];       // length + 2 (CRC), max LoRa payload is 255
    std::size_t   length;           // bytes of actual payload (excluding CRC)
    bool          has_crc;
    bool          crc_ok;
    std::uint16_t crc_received;     // big-endian uint16 of the trailing 2 bytes
    std::uint16_t crc_computed;
    std::uint64_t sample_index;
    std::uint32_t payload_symbol_count;

    // -- Diagnostics ----------------------------------------------------
    // Raw payload symbol values (post CFO correction, 0..N-1) and the
    // packed bytes BEFORE dewhitening. These let us cross-check the
    // pipeline against gr-lora_sdr / SDRangel reference decoders.
    std::uint16_t raw_symbols[64];
    std::size_t   raw_symbol_count;
    std::uint8_t  raw_bytes[260];
    std::size_t   raw_byte_count;
};

class ChirpChatRx {
public:
    using EventCallback       = std::function<void(const PreambleEvent&)>;
    using SymbolEventCallback = std::function<void(const SymbolEvent&)>;
    using HeaderEventCallback = std::function<void(const HeaderEvent&)>;
    using PayloadEventCallback = std::function<void(const PayloadEvent&)>;

    // `chip_rate_hz` must equal the LoRa bandwidth (samples-per-second feeding
    // process()). `oversampling` is reserved for future use; currently only 1
    // is supported (caller is expected to decimate to chip rate).
    ChirpChatRx(std::uint8_t spreading_factor,
                std::uint32_t chip_rate_hz,
                std::uint8_t sync_word = 0x2B);

    void set_event_callback(EventCallback cb) { cb_ = std::move(cb); }
    void set_symbol_callback(SymbolEventCallback cb) { sym_cb_ = std::move(cb); }
    void set_header_callback(HeaderEventCallback cb) { hdr_cb_ = std::move(cb); }
    void set_payload_callback(PayloadEventCallback cb) { pay_cb_ = std::move(cb); }

    // Feed chip-rate IQ samples. Thread-affine (call from a single thread).
    void process(std::span<const std::complex<float>> samples);

    // Reset all detector state (e.g. on retune).
    void reset();

    [[nodiscard]] std::uint8_t spreading_factor() const noexcept { return sf_; }
    [[nodiscard]] std::uint32_t chip_rate_hz()    const noexcept { return chip_rate_; }
    [[nodiscard]] int n() const noexcept { return n_; }

    // Diagnostics: total number of completed symbols since construction or
    // last reset(). Useful for tests.
    [[nodiscard]] std::uint64_t symbols_processed() const noexcept { return symbols_processed_; }
    [[nodiscard]] std::uint64_t preambles_detected() const noexcept { return preambles_detected_; }

    // Diagnostics: integer carrier-frequency offset (CFO) and sample-timing
    // offset (STO), in bins/samples, recovered at the last SFD lock by
    // disentangling the preamble up-chirp bin from the SFD down-chirp bin.
    // Both are signed (mod N, folded into [-N/2, N/2)). Exposed for tests
    // and for the UI to display the recovered sync parameters.
    [[nodiscard]] int last_cfo_int() const noexcept { return cfo_int_; }
    [[nodiscard]] int last_sto_int() const noexcept { return sto_int_; }

    // Disentangle integer CFO and STO from the preamble up-chirp peak bin and
    // the SFD down-chirp peak bin (ports SDRangel's Meshtastic sync math):
    //   up_bin   = (CFO - STO) mod N
    //   down_bin = (CFO + STO) mod N
    // => CFO = (up_bin + down_bin)/2,  STO = (down_bin - up_bin)/2
    // Both outputs are folded into the signed range [-N/2, N/2).
    static void disentangle_cfo_sto(int up_bin, int down_bin, int n,
                                    int& cfo_int, int& sto_int);

private:
    void emit_symbol_(int peak_bin, float peak_db, float peak_frac, std::uint64_t first_sample_index);
    void reset_frame_state_();
    // Per-symbol CFO retrack: small EMA correction of the NCO based on
    // each symbol's residual sub-bin frequency offset.
    void retrack_cfo_(float peak_frac, float peak_db);
    void decode_header_();
    void decode_payload_();

    // Compute the magnitude-spectrum peak strength of the current rolling
    // buffer multiplied by the *upchirp* template. The SFD (start-of-frame
    // delimiter) is 2.25 downchirps; multiplying downchirps by an upchirp
    // dechirps them to a CW tone, producing a strong peak. Used to find the
    // SFD position to sample-accuracy after preamble lock. Also records the
    // peak bin in `sfd_down_bin_` for CFO/STO disentanglement.
    [[nodiscard]] float upchirp_peak_db_();

    // Dechirp the current rolling window with `templ` (downchirp_ for the
    // preamble ramp, upchirp_ for the SFD ramp), FFT, and return the peak
    // bin's magnitude-squared. `out_bin` receives the peak bin. Used by the
    // SFD power-crossover detector.
    [[nodiscard]] float peak_magsq_(const std::vector<std::complex<float>>& templ,
                                    int& out_bin);

    std::uint8_t  sf_;
    std::uint32_t chip_rate_;
    std::uint8_t  sync_word_;
    int           n_;             // 2^SF
    std::uint64_t sample_index_{}; // running count of samples consumed

    std::vector<std::complex<float>> downchirp_; // length N
    std::vector<std::complex<float>> upchirp_;   // length N (= conj(downchirp_))
    std::vector<std::complex<float>> sym_buf_;   // length N (rolling window of samples)
    std::vector<std::complex<float>> fft_buf_;   // length N (working buffer for FFT)
    int                               sym_pos_{0}; // 0..N-1, next write slot in sym_buf_
    int                               sym_filled_{0}; // 0..N
    int                               stride_{0};    // counter between symbol emissions

    mrf::dsp::Fft fft_;

    // Preamble tracking — running window of recent peak bins.
    std::deque<int>   recent_bins_;
    std::deque<float> recent_peaks_;
    std::deque<float> recent_fracs_;
    int               last_locked_bin_{-1};

    EventCallback cb_;
    SymbolEventCallback sym_cb_;
    HeaderEventCallback hdr_cb_;
    PayloadEventCallback pay_cb_;
    std::uint64_t symbols_processed_{};
    std::uint64_t preambles_detected_{};

    // Post-preamble tracking. When > 0, every emitted symbol is forwarded as
    // a SymbolEvent until the budget is exhausted, then we re-arm the
    // preamble detector.
    int tracking_remaining_{0};
    int tracking_index_{0};

    // Frame state machine after preamble lock.
    enum class State : std::uint8_t {
        Hunting,        // Looking for preamble (default)
        SfdSearch,      // Preamble locked; waiting for SFD downchirps
        HeaderCapture,  // SFD locked; collecting 8 header symbols
        PayloadCapture, // Header decoded; collecting payload symbols
    };
    State state_{State::Hunting};
    int   sfd_consecutive_{0};   // upchirp-FFT peaks seen in a row
    int   sfd_search_budget_{0}; // give up if no SFD found in N symbols
    int   frame_symbol_count_{0}; // post-SFD symbols consumed by current frame
    int   cfo_bin_{0};           // preamble bin = (CFO - STO), subtract from symbols
    int   sfd_down_bin_{0};      // SFD down-chirp bin = (CFO + STO)
    int   cfo_int_{0};           // disentangled integer CFO (signed), = symbol correction
    int   sto_int_{0};           // disentangled integer STO (signed), window realignment
    float cfo_frac_{0.0f};       // fractional bin offset in [-0.5, +0.5]
    // Post-lock NCO state: phase increment (per sample) and accumulator.
    // Used to apply a fine frequency shift `e^{-j 2π cfo_frac / N · n}`
    // to each incoming sample so subsequent FFTs land on integer bins.
    double nco_phase_{0.0};
    double nco_phase_inc_{0.0};

    // Header symbol capture: 8 symbols after the SFD form the LoRa explicit
    // header at CR=4/8 with sf_app = sf - 2. We capture a few EXTRA symbols
    // (slack) so decode_header_ can slide the 8-symbol decode window and pick
    // the start offset whose header CRC validates — this corrects a coarse
    // timing/anchor error (the SFD crossover can land a symbol or two early),
    // which a constant bin-value delta cannot fix. The leftover captured
    // symbols past the chosen header window are the first payload symbols and
    // are forwarded to PayloadCapture so payload alignment is preserved.
    static constexpr int kHeaderSymbols   = 8;
    static constexpr int kHeaderSlack     = 6; // extra symbols for anchor search
    static constexpr int kHeaderCapture   = kHeaderSymbols + kHeaderSlack;
    static constexpr int kFrameSymbolMax  = 768;
    static constexpr int kSfdSearchMaxSymbols = 16; // give up after this many symbols
    // The SFD search slides at N/kSfdSearchOversample samples to find the
    // window aligned with the SFD down-chirp (see process()). The budget is
    // counted in evaluations, so it is scaled by this factor to preserve the
    // same wall-clock search span of kSfdSearchMaxSymbols symbols.
    static constexpr int kSfdSearchOversample = 4;
    // Threshold for an upchirp-FFT peak to be considered a real SFD
    // down-chirp. We anchor on the FIRST down-chirp, so this needs to be
    // strict enough that the (low-amplitude) sync-word residuals or noise
    // can't trigger a false lock. 12 dB is well above the ~5 dB observed
    // for sync words and well below the ~20 dB observed for real SFD
    // down-chirps in clean captures.
    static constexpr float kSfdPeakDbThreshold = 12.0f;
    std::vector<int> header_symbols_;            // captured raw bins
    std::uint64_t    header_first_sample_{};
    int              chosen_header_start_{0};    // anchor offset locked in decode_header_
    int              chosen_header_delta_{0};    // bin-value delta locked in decode_header_

    // Payload state: copied out of the decoded header so PayloadCapture
    // knows when to stop and how to decode.
    std::vector<int> payload_symbols_;
    int              payload_total_symbols_{0};   // expected count
    std::uint8_t     payload_length_bytes_{0};    // from header
    std::uint8_t     payload_coding_rate_{0};     // 1..4
    bool             payload_has_crc_{false};
    bool             payload_ldro_{false};        // ppm = sf - (2 if true else 0)
    std::uint64_t    payload_first_sample_{};
    // The header interleaver produces (sf-2) codewords from 8 symbols; the
    // first 5 are header bytes, the remaining (sf-7) are payload nibbles
    // ("leak"). We whiten + Hamming-decode them here so decode_payload_()
    // can prepend them when assembling the payload byte stream.
    std::vector<std::uint8_t> header_leak_nibbles_;
};

} // namespace mrf::modem
