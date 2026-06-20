// SPDX-License-Identifier: GPL-3.0-or-later
//
// LoRa / "Meshtastic" receiver front-end. This is a port of SDRangel's
// Meshtastic demodulator sink (plugins/channelrx/demodmeshtastic/
// meshtasticdemodsink.cpp) which itself ports the gr-lora_sdr frame-sync /
// fft-demod chain. It replaces the earlier `ChirpChatRx` SFD power-crossover
// heuristic with a deterministic three-state synchronizer:
//
//   1. Detect  — count consecutive identical preamble up-chirps, recover the
//                coarse preamble bin (kHat) via a histogram.
//   2. Sync    — estimate the fractional CFO (Bernier estimator) and
//                fractional STO from the stored preamble up-chirps, read the
//                two network-ID (sync-word) chirps and the 2.25 SFD
//                down-chirps, and recover the INTEGER CFO from the
//                down-chirp bin (CFO_int = floor(downVal/2)). A per-frame
//                "payload down-chirp" is then constructed that bakes in the
//                integer + fractional CFO so every data symbol dechirps onto
//                an integer FFT bin.
//   3. Data    — dechirp each symbol against the payload down-chirp, apply
//                the fractional STO as a sample shift, and collect raw symbol
//                bins. After 8 symbols the LoRa explicit header is tried
//                (offset 0..2 × delta -2..2); on a valid header CRC the
//                expected symbol count is computed and the payload is decoded
//                when complete.
//
// Input is consumed at the LoRa chip rate (= bandwidth, oversampling = 1):
// each std::complex<float> sample is one chip period. The header/payload
// FEC backend (Gray demap, diagonal de-interleave, Hamming, whitening,
// CRC) is shared with the rest of the modem via mrf::modem::lora and the
// event structs are reused from ChirpChatRx.h so the LoraModem logging /
// callback layer is unchanged.

#pragma once

#include "mrf/dsp/Fft.h"
#include "mrf/modem/ChirpChatRx.h" // reuse Preamble/Symbol/Header/Payload events
#include "mrf/modem/Preset.h"

#include <complex>
#include <cstdint>
#include <deque>
#include <functional>
#include <span>
#include <string>
#include <vector>

namespace mrf::modem {

class MeshtasticRx {
public:
    using EventCallback        = std::function<void(const PreambleEvent&)>;
    using SymbolEventCallback  = std::function<void(const SymbolEvent&)>;
    using HeaderEventCallback  = std::function<void(const HeaderEvent&)>;
    using PayloadEventCallback = std::function<void(const PayloadEvent&)>;

    // `chip_rate_hz` is the LoRa bandwidth (one chip per chip-period).
    // `oversampling` is the number of input samples per chip: the caller
    // must feed IQ at chip_rate_hz * oversampling. Oversampling >= 2 is
    // required to separate integer CFO from STO during sync (at os = 1 the
    // fractional-STO sample shift collapses to 0/±1 and timing never locks).
    MeshtasticRx(std::uint8_t spreading_factor,
                 std::uint32_t chip_rate_hz,
                 int oversampling = 4,
                 std::uint8_t sync_word = 0x2B);

    void set_event_callback(EventCallback cb) { cb_ = std::move(cb); }
    void set_symbol_callback(SymbolEventCallback cb) { sym_cb_ = std::move(cb); }
    void set_header_callback(HeaderEventCallback cb) { hdr_cb_ = std::move(cb); }
    void set_payload_callback(PayloadEventCallback cb) { pay_cb_ = std::move(cb); }

    // Feed chip-rate IQ samples (oversampling = 1). Thread-affine.
    void process(std::span<const std::complex<float>> samples);

    void reset();

    [[nodiscard]] std::uint8_t spreading_factor() const noexcept { return sf_; }
    [[nodiscard]] std::uint32_t chip_rate_hz() const noexcept { return chip_rate_; }
    [[nodiscard]] int n() const noexcept { return n_; }

    [[nodiscard]] std::uint64_t symbols_processed() const noexcept { return symbols_processed_; }
    [[nodiscard]] std::uint64_t preambles_detected() const noexcept { return preambles_detected_; }
    [[nodiscard]] int last_cfo_int() const noexcept { return cfo_int_; }
    [[nodiscard]] int last_sto_int() const noexcept { return sto_int_; }

private:
    // --- DSP primitives (port of the SDRangel sink members) ------------
    int  lora_mod_(int a, int b) const noexcept;
    int  lora_round_(float x) const noexcept;
    // Dechirp `samples[0..N)` with `ref_chirp`, FFT, return the peak bin.
    unsigned int get_symbol_val_(const std::complex<float>* samples,
                                 const std::complex<float>* ref_chirp);
    // Bernier fractional-CFO estimator over the stored preamble up-chirps.
    float estimate_cfo_frac_();
    // Fractional-STO estimator (2N FFT over the CFO-corrected preamble).
    float estimate_sto_frac_();
    void  build_payload_downchirp_();
    void  reset_frame_sync_();
    // One synchronizer step over the head of the sample FIFO. Returns the
    // number of samples to consume from the FIFO front.
    int   process_frame_sync_step_();

    // --- FEC backend (shared with ChirpChatRx) -------------------------
    void decode_header_();
    void decode_payload_();

    // --- Config --------------------------------------------------------
    std::uint8_t  sf_;
    std::uint32_t chip_rate_;
    std::uint8_t  sync_word_;
    int           n_;        // 2^SF (chips / FFT bins per symbol)
    int           os_factor_;     // input samples per chip (oversampling)
    int           os_center_phase_; // os_factor_/2 (decimation phase)
    int           symbol_span_;   // n_ * os_factor_ (input samples per symbol)
    int           nb_symbols_eff_; // 2^(SF - deBits)
    int           de_bits_;  // LDRO reduction bits (0 or 2)

    // Reference chirps, duplicated to 2N so we can dechirp from any offset.
    std::vector<std::complex<float>> up_chirps_;   // length 2N
    std::vector<std::complex<float>> down_chirps_; // length 2N
    mrf::dsp::Fft fft_;
    mrf::dsp::Fft fft2n_;

    // --- Streaming state ----------------------------------------------
    std::deque<std::complex<float>> fifo_;
    std::uint64_t sample_index_{};

    enum class State : std::uint8_t { Detect, Sync, Data };
    enum class SyncState : std::uint8_t {
        NetId1, NetId2, Downchirp1, Downchirp2, QuarterDown
    };
    State     state_{State::Detect};
    SyncState sync_state_{SyncState::NetId1};

    // Detect-state preamble tracking.
    int symbol_cnt_{1};
    int bin_idx_{0};
    int k_hat_{0};
    std::vector<int> preamble_vals_;
    std::vector<std::complex<float>> preamble_raw_;     // N * required upchirps
    std::vector<std::complex<float>> preamble_upchirps_; // CFO-frac corrected
    std::vector<std::complex<float>> in_down_;           // N working window
    std::vector<std::complex<float>> sym_corr_;          // N working window
    std::vector<std::complex<float>> cfo_frac_correc_;   // N
    std::vector<std::complex<float>> payload_downchirp_; // N
    std::vector<std::complex<float>> net_id_samp_;       // sync-word window

    // Recovered sync parameters.
    int   cfo_int_{0};
    int   sto_int_{0};
    int   down_val_{0};
    int   net_ids_[2]{0, 0};
    float cfo_frac_{0.0f};
    float sto_frac_{0.0f};
    bool  cfo_sto_estimated_{false};
    int   additional_upchirps_{0};
    float last_peak_db_{0.0f}; // peak/mean ratio from the last FFT (for logging)

    int required_upchirps_;
    int up_symb_to_use_;

    // --- Frame collection ---------------------------------------------
    int frame_symbol_count_{0};
    int expected_symbols_{0};
    bool header_locked_{false};
    std::uint64_t frame_first_sample_{};

    // FEC backend state (reused from ChirpChatRx semantics). Symbols pushed
    // here are already CFO-corrected (the payload down-chirp bakes the CFO
    // in), so cfo_bin_ stays 0 and the header delta-search absorbs residual.
    int cfo_bin_{0};
    std::vector<int> header_symbols_;
    std::uint64_t    header_first_sample_{};
    int  chosen_header_start_{0};
    int  chosen_header_delta_{0};
    std::vector<int> payload_symbols_;
    int          payload_total_symbols_{0};
    std::uint8_t payload_length_bytes_{0};
    std::uint8_t payload_coding_rate_{0};
    bool         payload_has_crc_{false};
    bool         payload_ldro_{false};
    std::uint64_t payload_first_sample_{};
    std::vector<std::uint8_t> header_leak_nibbles_;

    static constexpr int kHeaderSymbols = 8;
    static constexpr int kHeaderSlack   = 2; // SDRangel tryHeaderLock offset 0..2
    static constexpr int kNbSymbolsMax  = 768;

    // Counters / callbacks.
    std::uint64_t symbols_processed_{};
    std::uint64_t preambles_detected_{};
    EventCallback        cb_;
    SymbolEventCallback  sym_cb_;
    HeaderEventCallback  hdr_cb_;
    PayloadEventCallback pay_cb_;
};

} // namespace mrf::modem
