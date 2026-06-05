// SPDX-License-Identifier: GPL-3.0-or-later
//
// Port of SDRangel's MeshtasticDemodSink LoRa frame synchronizer
// (plugins/channelrx/demodmeshtastic/meshtasticdemodsink.cpp) adapted to a
// chip-rate (oversampling = 1) sample stream. See MeshtasticRx.h for the
// algorithm overview. The header/payload FEC backend (decode_header_ /
// decode_payload_) is shared verbatim with the previous ChirpChatRx so the
// proven Gray / de-interleave / Hamming / whitening / CRC path is reused.

#include "mrf/modem/MeshtasticRx.h"
#include "mrf/modem/LoraDecoder.h"

#include <algorithm>
#include <cmath>
#include <numbers>
#include <stdexcept>
#include <vector>

namespace mrf::modem {

namespace {
constexpr double kPi    = std::numbers::pi;
constexpr double kTwoPi = 2.0 * std::numbers::pi;
} // namespace

MeshtasticRx::MeshtasticRx(std::uint8_t spreading_factor,
                           std::uint32_t chip_rate_hz,
                           int oversampling,
                           std::uint8_t sync_word)
    : sf_(spreading_factor),
      chip_rate_(chip_rate_hz),
      sync_word_(sync_word),
      n_(static_cast<int>(1ULL << spreading_factor)),
      os_factor_(oversampling > 0 ? oversampling : 1),
      os_center_phase_(oversampling > 1 ? oversampling / 2 : 0),
      symbol_span_(static_cast<int>(1ULL << spreading_factor) *
                   (oversampling > 0 ? oversampling : 1)),
      fft_(static_cast<std::size_t>(1ULL << spreading_factor)),
      fft2n_(static_cast<std::size_t>(2ULL << spreading_factor)) {
    if (spreading_factor < 5 || spreading_factor > 12)
        throw std::invalid_argument("MeshtasticRx: spreading_factor out of range");
    if (chip_rate_hz == 0)
        throw std::invalid_argument("MeshtasticRx: chip_rate_hz must be > 0");

    // LDRO is enabled when the symbol time T_sym = 2^SF / BW >= 16 ms.
    const double t_sym_ms = 1000.0 * static_cast<double>(n_) /
                            static_cast<double>(chip_rate_);
    de_bits_       = (t_sym_ms >= 16.0) ? 2 : 0;
    nb_symbols_eff_ = 1 << (sf_ - de_bits_);

    // Canonical gr-lora_sdr reference chirps, duplicated to 2N so the FFT
    // window can be taken from any offset within a symbol.
    up_chirps_.assign(static_cast<std::size_t>(2 * n_), {0.0f, 0.0f});
    down_chirps_.assign(static_cast<std::size_t>(2 * n_), {0.0f, 0.0f});
    for (int i = 0; i < n_; ++i) {
        const double nD = static_cast<double>(i);
        const double N  = static_cast<double>(n_);
        const double phase = kTwoPi * ((nD * nD) / (2.0 * N) - 0.5 * nD);
        up_chirps_[static_cast<std::size_t>(i)] =
            {static_cast<float>(std::cos(phase)), static_cast<float>(std::sin(phase))};
        down_chirps_[static_cast<std::size_t>(i)] =
            std::conj(up_chirps_[static_cast<std::size_t>(i)]);
    }
    std::copy(up_chirps_.begin(), up_chirps_.begin() + n_, up_chirps_.begin() + n_);
    std::copy(down_chirps_.begin(), down_chirps_.begin() + n_, down_chirps_.begin() + n_);

    // Number of preamble up-chirps we require to confirm a lock. The over-
    // the-air preamble is 16 chirps; we lock well before it ends.
    required_upchirps_ = 6;
    up_symb_to_use_    = required_upchirps_ - 1;

    preamble_vals_.assign(static_cast<std::size_t>(required_upchirps_), 0);
    preamble_raw_.assign(static_cast<std::size_t>(n_ * required_upchirps_), {0.0f, 0.0f});
    preamble_upchirps_.assign(static_cast<std::size_t>(n_ * required_upchirps_), {0.0f, 0.0f});
    in_down_.assign(static_cast<std::size_t>(n_), {0.0f, 0.0f});
    sym_corr_.assign(static_cast<std::size_t>(n_), {0.0f, 0.0f});
    cfo_frac_correc_.assign(static_cast<std::size_t>(n_), {1.0f, 0.0f});
    payload_downchirp_.assign(static_cast<std::size_t>(n_), {1.0f, 0.0f});
    // Sync-word sample window, sized as in the SDRangel sink:
    // (symbolSpan * 5) / 2 + symbolSpan.
    net_id_samp_.assign(static_cast<std::size_t>((symbol_span_ * 5) / 2 + symbol_span_),
                        {0.0f, 0.0f});
}

void MeshtasticRx::reset() {
    fifo_.clear();
    sample_index_ = 0;
    symbols_processed_ = 0;
    preambles_detected_ = 0;
    reset_frame_sync_();
}

void MeshtasticRx::reset_frame_sync_() {
    fifo_.clear();
    state_ = State::Detect;
    sync_state_ = SyncState::NetId1;
    symbol_cnt_ = 1;
    bin_idx_ = 0;
    k_hat_ = 0;
    cfo_int_ = 0;
    sto_int_ = 0;
    down_val_ = 0;
    net_ids_[0] = net_ids_[1] = 0;
    cfo_frac_ = 0.0f;
    sto_frac_ = 0.0f;
    cfo_sto_estimated_ = false;
    additional_upchirps_ = 0;
    frame_symbol_count_ = 0;
    expected_symbols_ = 0;
    header_locked_ = false;
    cfo_bin_ = 0;
    header_symbols_.clear();
    payload_symbols_.clear();
    payload_total_symbols_ = 0;
    chosen_header_start_ = 0;
    chosen_header_delta_ = 0;
    header_leak_nibbles_.clear();
    std::fill(preamble_vals_.begin(), preamble_vals_.end(), 0);
    std::fill(cfo_frac_correc_.begin(), cfo_frac_correc_.end(), std::complex<float>{1.0f, 0.0f});
}

int MeshtasticRx::lora_mod_(int a, int b) const noexcept {
    if (b <= 0) return 0;
    return (a % b + b) % b;
}

int MeshtasticRx::lora_round_(float x) const noexcept {
    return (x > 0.0f) ? static_cast<int>(x + 0.5f)
                      : static_cast<int>(std::ceil(x - 0.5f));
}

unsigned int MeshtasticRx::get_symbol_val_(const std::complex<float>* samples,
                                           const std::complex<float>* ref_chirp) {
    std::vector<std::complex<float>> buf(static_cast<std::size_t>(n_));
    for (int i = 0; i < n_; ++i) {
        buf[static_cast<std::size_t>(i)] =
            samples[i] * ref_chirp[i];
    }
    fft_.forward(std::span<std::complex<float>>(buf.data(), buf.size()));
    double peak = 0.0, total = 0.0;
    unsigned int imax = 0;
    for (int i = 0; i < n_; ++i) {
        const double m = std::norm(buf[static_cast<std::size_t>(i)]);
        total += m;
        if (m > peak) { peak = m; imax = static_cast<unsigned int>(i); }
    }
    const double mean_rest = (total - peak) /
        static_cast<double>(std::max(1, n_ - 1));
    last_peak_db_ = (peak > 0.0 && mean_rest > 0.0)
        ? static_cast<float>(10.0 * std::log10(peak / mean_rest))
        : 0.0f;
    return imax;
}

float MeshtasticRx::estimate_cfo_frac_() {
    if (up_symb_to_use_ <= 1) return 0.0f;
    const int cfo_start = std::max(0, n_ - k_hat_);
    if (static_cast<std::size_t>(cfo_start) >= preamble_raw_.size()) return 0.0f;
    const std::complex<float>* base = preamble_raw_.data() + cfo_start;
    // Guard: don't read past the stored preamble.
    const std::size_t avail = preamble_raw_.size() - static_cast<std::size_t>(cfo_start);

    std::vector<int>    k0(static_cast<std::size_t>(up_symb_to_use_), 0);
    std::vector<double> k0_mag(static_cast<std::size_t>(up_symb_to_use_), 0.0);
    std::vector<std::complex<float>> fft_val(
        static_cast<std::size_t>(up_symb_to_use_) * static_cast<std::size_t>(n_));
    std::vector<std::complex<float>> buf(static_cast<std::size_t>(n_));

    for (int i = 0; i < up_symb_to_use_; ++i) {
        if (static_cast<std::size_t>((i + 1) * n_) > avail) break;
        const std::complex<float>* sym = base + i * n_;
        for (int j = 0; j < n_; ++j)
            buf[static_cast<std::size_t>(j)] = sym[j] * down_chirps_[static_cast<std::size_t>(j)];
        fft_.forward(std::span<std::complex<float>>(buf.data(), buf.size()));
        double peak = 0.0; unsigned int imax = 0;
        for (int j = 0; j < n_; ++j) {
            const double m = std::norm(buf[static_cast<std::size_t>(j)]);
            if (m > peak) { peak = m; imax = static_cast<unsigned int>(j); }
            fft_val[static_cast<std::size_t>(j + i * n_)] = buf[static_cast<std::size_t>(j)];
        }
        k0[static_cast<std::size_t>(i)] = static_cast<int>(imax);
        k0_mag[static_cast<std::size_t>(i)] = peak;
    }

    const int idx_max = k0[static_cast<std::size_t>(
        std::distance(k0_mag.begin(), std::max_element(k0_mag.begin(), k0_mag.end())))];
    std::complex<float> four_cum(0.0f, 0.0f);
    for (int i = 0; i < up_symb_to_use_ - 1; ++i) {
        four_cum += fft_val[static_cast<std::size_t>(idx_max + n_ * i)] *
                    std::conj(fft_val[static_cast<std::size_t>(idx_max + n_ * (i + 1))]);
    }
    const float cfo_frac = -std::arg(four_cum) / static_cast<float>(kTwoPi);

    // Build the CFO-frac-corrected preamble up-chirps for the STO estimate.
    const std::size_t corr_count =
        std::min<std::size_t>(static_cast<std::size_t>(up_symb_to_use_) * static_cast<std::size_t>(n_),
                              std::min(avail, preamble_upchirps_.size()));
    for (std::size_t nIdx = 0; nIdx < corr_count; ++nIdx) {
        const float phase = -static_cast<float>(kTwoPi) * cfo_frac *
                            static_cast<float>(nIdx) / static_cast<float>(n_);
        preamble_upchirps_[nIdx] = base[nIdx] * std::complex<float>(std::cos(phase), std::sin(phase));
    }
    return cfo_frac;
}

float MeshtasticRx::estimate_sto_frac_() {
    if (up_symb_to_use_ <= 0) return 0.0f;
    const int fft2n = 2 * n_;
    std::vector<double> mag_sq(static_cast<std::size_t>(fft2n), 0.0);
    std::vector<std::complex<float>> buf(static_cast<std::size_t>(fft2n));

    for (int i = 0; i < up_symb_to_use_; ++i) {
        if (static_cast<std::size_t>((i + 1) * n_) > preamble_upchirps_.size()) break;
        const std::complex<float>* sym = preamble_upchirps_.data() + i * n_;
        for (int j = 0; j < n_; ++j)
            buf[static_cast<std::size_t>(j)] = sym[j] * down_chirps_[static_cast<std::size_t>(j)];
        std::fill(buf.begin() + n_, buf.end(), std::complex<float>{0.0f, 0.0f});
        fft2n_.forward(std::span<std::complex<float>>(buf.data(), buf.size()));
        for (int j = 0; j < fft2n; ++j)
            mag_sq[static_cast<std::size_t>(j)] += std::norm(buf[static_cast<std::size_t>(j)]);
    }

    const int k0 = static_cast<int>(std::distance(
        mag_sq.begin(), std::max_element(mag_sq.begin(), mag_sq.end())));
    const double y_1 = mag_sq[static_cast<std::size_t>(lora_mod_(k0 - 1, fft2n))];
    const double y0  = mag_sq[static_cast<std::size_t>(k0)];
    const double y1  = mag_sq[static_cast<std::size_t>(lora_mod_(k0 + 1, fft2n))];
    const double u = 64.0 * static_cast<double>(n_) / 406.5506497;
    const double v = u * 2.4674;
    const double wa = (y1 - y_1) / (u * (y1 + y_1) + v * y0 + 1e-12);
    const double ka = wa * static_cast<double>(n_) / kPi;
    const double kres = std::fmod((k0 + ka) / 2.0, 1.0);
    return static_cast<float>(kres - (kres > 0.5 ? 1.0 : 0.0));
}

void MeshtasticRx::build_payload_downchirp_() {
    const int N  = n_;
    const int id = lora_mod_(cfo_int_, N);
    for (int n = 0; n < N; ++n) {
        const int n_fold = N - id;
        const double nD = static_cast<double>(n);
        const double ND = static_cast<double>(N);
        double phase;
        if (n < n_fold) {
            phase = kTwoPi * ((nD * nD) / (2.0 * ND) +
                              (static_cast<double>(id) / ND - 0.5) * nD);
        } else {
            phase = kTwoPi * ((nD * nD) / (2.0 * ND) +
                              (static_cast<double>(id) / ND - 1.5) * nD);
        }
        std::complex<float> up(static_cast<float>(std::cos(phase)),
                               static_cast<float>(std::sin(phase)));
        std::complex<float> ref = std::conj(up); // m_invertRamps == false
        const float cfo_phase = -static_cast<float>(kTwoPi) * cfo_frac_ *
                                static_cast<float>(n) / static_cast<float>(N);
        ref *= std::complex<float>(std::cos(cfo_phase), std::sin(cfo_phase));
        payload_downchirp_[static_cast<std::size_t>(n)] = ref;
    }
}

void MeshtasticRx::process(std::span<const std::complex<float>> samples) {
    for (const auto& s : samples) {
        fifo_.push_back(s);
        ++sample_index_;

        while (true) {
            const std::size_t needed = (state_ == State::Sync)
                                           ? static_cast<std::size_t>(3 * symbol_span_)
                                           : static_cast<std::size_t>(symbol_span_);
            if (fifo_.size() < needed) break;

            int consumed = process_frame_sync_step_();
            if (consumed <= 0) consumed = 1;
            consumed = std::min(consumed, static_cast<int>(fifo_.size()));
            for (int i = 0; i < consumed; ++i) fifo_.pop_front();
        }
    }
}

int MeshtasticRx::process_frame_sync_step_() {
    const int sto_shift = lora_round_(sto_frac_ * static_cast<float>(os_factor_));

    // Decimate the oversampled FIFO head to the N-length working window:
    // pick one sample per chip at the center phase, shifted by the fractional
    // STO (in oversampled samples).
    for (int ii = 0; ii < n_; ++ii) {
        int idx = os_center_phase_ + os_factor_ * ii - sto_shift;
        idx = std::max(0, std::min(idx, symbol_span_ - 1));
        in_down_[static_cast<std::size_t>(ii)] = fifo_[static_cast<std::size_t>(idx)];
    }

    // ---------------- Detect: preamble up-chirp tracking ---------------
    if (state_ == State::Detect) {
        const int bin_new =
            static_cast<int>(get_symbol_val_(in_down_.data(), down_chirps_.data()));
        const int detect_delta =
            std::abs(lora_mod_(std::abs(bin_new - bin_idx_) + 1, n_) - 1);
        const bool consecutive = (detect_delta <= 1);

        if (consecutive) {
            if (symbol_cnt_ == 1 && !preamble_vals_.empty())
                preamble_vals_[0] = bin_idx_;
            if (symbol_cnt_ >= 0 && symbol_cnt_ < static_cast<int>(preamble_vals_.size()))
                preamble_vals_[static_cast<std::size_t>(symbol_cnt_)] = bin_new;
            const std::size_t off = static_cast<std::size_t>(symbol_cnt_) * static_cast<std::size_t>(n_);
            if (off + static_cast<std::size_t>(n_) <= preamble_raw_.size())
                std::copy_n(in_down_.begin(), n_, preamble_raw_.begin() + off);
            ++symbol_cnt_;
        } else {
            if (preamble_raw_.size() >= static_cast<std::size_t>(n_))
                std::copy_n(in_down_.begin(), n_, preamble_raw_.begin());
            symbol_cnt_ = 1;
        }
        bin_idx_ = bin_new;

        if (symbol_cnt_ >= required_upchirps_ && !preamble_vals_.empty()) {
            // Recover the coarse preamble bin (kHat) by majority vote.
            std::vector<unsigned int> hist(static_cast<std::size_t>(n_), 0U);
            unsigned int best_bin = 0U, best_count = 0U;
            for (int v : preamble_vals_) {
                const unsigned int b = static_cast<unsigned int>(lora_mod_(v, n_));
                const unsigned int c = ++hist[b];
                if (c > best_count) { best_count = c; best_bin = b; }
            }
            k_hat_ = static_cast<int>(best_bin);

            // Capture the network-ID (sync-word) sample window.
            const int net_start =
                static_cast<int>(0.75f * static_cast<float>(symbol_span_)) -
                k_hat_ * os_factor_;
            for (int i = 0; i < symbol_span_ / 4 && i < static_cast<int>(net_id_samp_.size()); ++i) {
                const int src = std::max(0, std::min(net_start + i, static_cast<int>(fifo_.size()) - 1));
                net_id_samp_[static_cast<std::size_t>(i)] = fifo_[static_cast<std::size_t>(src)];
            }

            additional_upchirps_ = 0;
            state_ = State::Sync;
            sync_state_ = SyncState::NetId1;
            symbol_cnt_ = 0;
            cfo_sto_estimated_ = false;
            ++preambles_detected_;

            // Emit the preamble-detected event for logging.
            if (cb_) {
                int signed_bin = k_hat_;
                if (signed_bin > n_ / 2) signed_bin -= n_;
                const float cfo_hz = static_cast<float>(signed_bin) *
                                     static_cast<float>(chip_rate_) / static_cast<float>(n_);
                cb_(PreambleEvent{k_hat_, cfo_hz, last_peak_db_, symbol_cnt_, frame_first_sample_});
            }
            return os_factor_ * (n_ - static_cast<int>(best_bin));
        }
        return symbol_span_;
    }

    // ---------------- Sync: CFO/STO estimate + SFD read ----------------
    if (state_ == State::Sync) {
        if (!cfo_sto_estimated_) {
            cfo_frac_ = estimate_cfo_frac_();
            sto_frac_ = estimate_sto_frac_();
            for (int n = 0; n < n_; ++n) {
                const float phase = -static_cast<float>(kTwoPi) * cfo_frac_ *
                                    static_cast<float>(n) / static_cast<float>(n_);
                cfo_frac_correc_[static_cast<std::size_t>(n)] =
                    std::complex<float>(std::cos(phase), std::sin(phase));
            }
            cfo_sto_estimated_ = true;
        }

        for (int i = 0; i < n_; ++i)
            sym_corr_[static_cast<std::size_t>(i)] =
                in_down_[static_cast<std::size_t>(i)] * cfo_frac_correc_[static_cast<std::size_t>(i)];

        const int bin_idx =
            static_cast<int>(get_symbol_val_(sym_corr_.data(), down_chirps_.data()));

        switch (sync_state_) {
        case SyncState::NetId1:
            if ((bin_idx == 0) || (bin_idx == 1) || (bin_idx == n_ - 1)) {
                additional_upchirps_ = std::min(additional_upchirps_ + 1, 3);
            } else {
                sync_state_ = SyncState::NetId2;
                net_ids_[0] = bin_idx;
            }
            break;
        case SyncState::NetId2:
            sync_state_ = SyncState::Downchirp1;
            net_ids_[1] = bin_idx;
            break;
        case SyncState::Downchirp1:
            sync_state_ = SyncState::Downchirp2;
            break;
        case SyncState::Downchirp2:
            down_val_ = static_cast<int>(get_symbol_val_(sym_corr_.data(), up_chirps_.data()));
            sync_state_ = SyncState::QuarterDown;
            break;
        case SyncState::QuarterDown:
        default:
            if (static_cast<unsigned int>(down_val_) < static_cast<unsigned int>(n_) / 2U)
                cfo_int_ = static_cast<int>(std::floor(down_val_ / 2.0));
            else
                cfo_int_ = static_cast<int>(std::floor((down_val_ - n_) / 2.0));
            sto_int_ = 0;

            // Refine the fractional STO now that the integer CFO is known.
            // Mirrors SDRangel's corrLen block: rotate the preamble up-chirps
            // by cfo_int (mod N), strip the residual integer-CFO phase ramp,
            // then re-run the STO estimator on the integer-CFO-corrected
            // up-chirps. The SFO sub-correction is a no-op here because the
            // frame-sync sample rate equals the bandwidth (bw/fs == 1) and
            // SFO_hat is 0, so it is omitted. The refined STO is only accepted
            // when it moves by less than (os-1)/os of a chip, matching the
            // reference guard. This matters at oversampling > 1: the un-refined
            // STO (estimated before integer-CFO removal) yields a wrong
            // sub-chip sto_shift that misaligns every payload symbol.
            {
                const int up_sym_count = std::min(
                    std::max(0, up_symb_to_use_),
                    static_cast<int>(preamble_upchirps_.size() / static_cast<std::size_t>(std::max(1, n_))));
                const int corr_len = up_sym_count * n_;
                if (corr_len > 0) {
                    const int cfo_int_mod = lora_mod_(cfo_int_, n_);
                    std::rotate(preamble_upchirps_.begin(),
                                preamble_upchirps_.begin() + cfo_int_mod,
                                preamble_upchirps_.begin() + corr_len);
                    for (int n = 0; n < corr_len; ++n) {
                        const float phase = -static_cast<float>(kTwoPi) *
                                            static_cast<float>(cfo_int_) *
                                            static_cast<float>(n) / static_cast<float>(n_);
                        preamble_upchirps_[static_cast<std::size_t>(n)] *=
                            std::complex<float>(std::cos(phase), std::sin(phase));
                    }
                    const float tmp_sto = estimate_sto_frac_();
                    const float diff_sto = sto_frac_ - tmp_sto;
                    if (std::abs(diff_sto) <=
                        (static_cast<float>(os_factor_) - 1.0f) / static_cast<float>(os_factor_)) {
                        sto_frac_ = tmp_sto;
                    }
                }
            }

            build_payload_downchirp_();

            // Begin frame data capture.
            frame_symbol_count_ = 0;
            expected_symbols_   = 0;
            header_locked_      = false;
            cfo_bin_            = 0;
            header_symbols_.clear();
            payload_symbols_.clear();
            payload_total_symbols_ = 0;
            header_leak_nibbles_.clear();
            frame_first_sample_ = sample_index_;
            header_first_sample_ = sample_index_;
            payload_first_sample_ = sample_index_;
            state_ = State::Data;
            sync_state_ = SyncState::NetId1;
            return std::max(1, symbol_span_ / 4 + os_factor_ * cfo_int_);
        }
        return symbol_span_;
    }

    // ---------------- Data: collect + decode header/payload ------------
    {
        const int raw_symbol =
            static_cast<int>(get_symbol_val_(in_down_.data(), payload_downchirp_.data()));
        ++symbols_processed_;
        ++frame_symbol_count_;

        if (!header_locked_) {
            // Bins are already CFO-corrected by payload_downchirp_, so the
            // value pushed to the FEC backend is the raw bin (cfo_bin_ == 0).
            header_symbols_.push_back(raw_symbol);
            if (static_cast<int>(header_symbols_.size()) == kHeaderSymbols + kHeaderSlack) {
                decode_header_();
                if (payload_total_symbols_ > 0) {
                    header_locked_ = true;
                    payload_symbols_.clear();
                    const int first_payload = chosen_header_start_ + kHeaderSymbols;
                    for (int i = first_payload;
                         i < static_cast<int>(header_symbols_.size()); ++i) {
                        const int v = ((header_symbols_[static_cast<std::size_t>(i)] +
                                        chosen_header_delta_) % n_ + n_) % n_;
                        payload_symbols_.push_back(v);
                    }
                    header_symbols_.clear();
                } else {
                    reset_frame_sync_();
                }
            }
        } else {
            const int v = ((raw_symbol + chosen_header_delta_) % n_ + n_) % n_;
            payload_symbols_.push_back(v);
            if (static_cast<int>(payload_symbols_.size()) >= payload_total_symbols_) {
                decode_payload_();
                reset_frame_sync_();
            }
        }

        // Abort runaway frames that never lock / finish.
        if (state_ == State::Data && frame_symbol_count_ >= kNbSymbolsMax) {
            reset_frame_sync_();
        }
        return symbol_span_;
    }
}

// ===========================================================================
//  FEC backend — shared verbatim with the previous ChirpChatRx decoder.
// ===========================================================================

void MeshtasticRx::decode_header_() {
    using namespace mrf::modem::lora;
    const std::uint8_t sf_app = static_cast<std::uint8_t>(sf_ - 2);
    const std::uint8_t cr_app = 8;

    static constexpr int kHeaderDeltas[] = {
        0, -1, 1, -2, 2, -3, 3, -4, 4, -5, 5, -6, 6, -7, 7, -8, 8};

    HeaderEvent ev{};
    std::vector<std::uint8_t> cws;
    int chosen_delta = 0;
    int chosen_start = 0;

    auto try_delta = [&](int start, int delta, HeaderEvent& out,
                         std::vector<std::uint8_t>& out_cws) {
        std::vector<std::uint16_t> sym_bits(kHeaderSymbols);
        for (int i = 0; i < kHeaderSymbols; ++i) {
            const int raw = ((header_symbols_[static_cast<std::size_t>(start + i)] + delta) % n_ + n_) % n_;
            sym_bits[static_cast<std::size_t>(i)] = symbol_to_bits(
                static_cast<std::uint16_t>(raw), sf_, /*ldro*/true);
        }
        out_cws = deinterleave(
            std::span<const std::uint16_t>(sym_bits.data(), sym_bits.size()),
            sf_app, cr_app);

        out = HeaderEvent{};
        out.sample_index = header_first_sample_;
        out.nibble_count = std::min<std::size_t>(out_cws.size(), 10);
        for (std::size_t i = 0; i < out.nibble_count; ++i) {
            bool corr = false;
            out.raw_nibbles[i] = hamming_decode(out_cws[i], 4, corr);
        }
        if (out_cws.size() < 5) return false;
        const std::uint8_t length = static_cast<std::uint8_t>(
            ((out.raw_nibbles[0] & 0x0F) << 4) | (out.raw_nibbles[1] & 0x0F));
        const std::uint8_t fec_info = static_cast<std::uint8_t>(out.raw_nibbles[2] & 0x0F);
        const std::uint8_t got_chk = static_cast<std::uint8_t>(
            ((out.raw_nibbles[3] & 0x0F) << 4) | (out.raw_nibbles[4] & 0x0F));
        const std::uint8_t expected = header_crc5(length, fec_info);
        out.payload_length = length;
        out.has_crc     = (fec_info & 0x01) != 0;
        out.coding_rate = static_cast<std::uint8_t>((fec_info >> 1) & 0x07);
        out.parity_ok   = (got_chk == expected);
        const bool sane =
            out.parity_ok &&
            out.has_crc &&
            out.coding_rate >= 1 && out.coding_rate <= 4 &&
            length >= 16 && length <= 255;
        return sane;
    };

    bool locked = false;
    for (int start = 0; start <= kHeaderSlack && !locked; ++start) {
        for (const int delta : kHeaderDeltas) {
            HeaderEvent cand{};
            std::vector<std::uint8_t> cand_cws;
            if (try_delta(start, delta, cand, cand_cws)) {
                ev = cand;
                cws = std::move(cand_cws);
                chosen_delta = delta;
                chosen_start = start;
                locked = true;
                break;
            }
            if (start == 0 && delta == 0) { ev = cand; cws = cand_cws; }
        }
    }
    chosen_header_start_ = chosen_start;
    chosen_header_delta_ = locked ? chosen_delta : 0;
    if (locked && chosen_delta != 0) {
        cfo_bin_ = ((cfo_bin_ - chosen_delta) % n_ + n_) % n_;
    }

    payload_total_symbols_ = 0;
    header_leak_nibbles_.clear();

    if (locked && ev.parity_ok && cws.size() >= 5) {
        if (ev.coding_rate >= 1 && ev.coding_rate <= 4) {
            const double t_sym_ms =
                1000.0 * static_cast<double>(n_) / static_cast<double>(chip_rate_);
            payload_ldro_ = (t_sym_ms >= 16.0);
            const int sf  = static_cast<int>(sf_);
            const int eff = sf - (payload_ldro_ ? 2 : 0);
            const int pl  = static_cast<int>(ev.payload_length);
            const int crc = ev.has_crc ? 1 : 0;
            const int cr  = static_cast<int>(ev.coding_rate);
            const int num = 8 * pl - 4 * sf + 28 + 16 * crc;
            const int den = 4 * eff;
            int blocks = (num + den - 1) / den;
            if (blocks < 0) blocks = 0;
            payload_total_symbols_ = blocks * (cr + 4);
            payload_length_bytes_  = ev.payload_length;
            payload_coding_rate_   = ev.coding_rate;
            payload_has_crc_       = ev.has_crc;

            header_leak_nibbles_.clear();
            if (cws.size() > 5) {
                for (std::size_t k = 5; k < cws.size(); ++k) {
                    bool corr = false;
                    header_leak_nibbles_.push_back(hamming_decode(cws[k], 4, corr) & 0x0F);
                }
            }
        } else {
            payload_total_symbols_ = 0;
            header_leak_nibbles_.clear();
        }
    }

    for (int i = 0; i < kHeaderSymbols; ++i) {
        ev.raw_symbols[i] = static_cast<std::uint16_t>(
            header_symbols_[static_cast<std::size_t>(chosen_start + i)]);
    }
    ev.cfo_int      = cfo_int_;
    ev.sto_int      = sto_int_;
    ev.chosen_delta = locked ? chosen_delta : 0;
    ev.chosen_start = locked ? chosen_start : 0;
    ev.k_hat        = k_hat_;
    ev.cfo_frac     = cfo_frac_;
    ev.sto_frac     = sto_frac_;
    ev.down_val     = down_val_;
    ev.add_upchirps = additional_upchirps_;
    ev.net_id0      = net_ids_[0];
    ev.net_id1      = net_ids_[1];

    if (hdr_cb_) hdr_cb_(ev);
}

void MeshtasticRx::decode_payload_() {
    using namespace mrf::modem::lora;

    const std::uint8_t ppm    = static_cast<std::uint8_t>(sf_ - (payload_ldro_ ? 2 : 0));
    const std::uint8_t cr     = payload_coding_rate_;
    const std::uint8_t cw_len = static_cast<std::uint8_t>(cr + 4);
    if (payload_symbols_.size() < static_cast<std::size_t>(payload_total_symbols_) ||
        cr < 1 || cr > 4 || ppm == 0) {
        return;
    }

    const int n_total = 1 << sf_;

    // Decode the collected payload symbols for a given integer-bin offset
    // (delta). Fills `out` and returns whether the payload CRC validated.
    // This mirrors SDRangel's MeshtasticDemodDecoder hard-decode retry, which
    // re-runs the payload decode with the symbols nudged by -1/+1 when the
    // first attempt fails CRC (a clean header but garbage payload usually
    // means the payload needs a different residual integer offset).
    auto decode_with_delta = [&](int delta, PayloadEvent& out) -> bool {
        std::vector<std::uint16_t> sym_bits(payload_symbols_.size());
        for (std::size_t i = 0; i < payload_symbols_.size(); ++i) {
            const int raw = static_cast<int>(payload_symbols_[i]) + delta;
            const std::uint16_t corr = static_cast<std::uint16_t>(
                ((raw - 1) % n_total + n_total) % n_total);
            sym_bits[i] = gray_demap(corr, sf_, ppm);
        }

        std::vector<std::uint8_t> codewords;
        codewords.reserve(static_cast<std::size_t>(payload_total_symbols_) / cw_len * ppm);
        const std::size_t total_blocks =
            static_cast<std::size_t>(payload_total_symbols_) / cw_len;
        for (std::size_t b = 0; b < total_blocks; ++b) {
            const std::span<const std::uint16_t> block(sym_bits.data() + b * cw_len, cw_len);
            auto cws = deinterleave(block, ppm, cw_len);
            for (auto cw : cws) codewords.push_back(cw);
        }

        std::vector<std::uint8_t> payload_nibbles;
        payload_nibbles.reserve(codewords.size());
        for (auto cw : codewords) {
            bool corr = false;
            payload_nibbles.push_back(hamming_decode(cw, cr, corr) & 0x0F);
        }

        std::vector<std::uint8_t> all_nibbles;
        all_nibbles.reserve(header_leak_nibbles_.size() + payload_nibbles.size());
        all_nibbles.insert(all_nibbles.end(), header_leak_nibbles_.begin(), header_leak_nibbles_.end());
        all_nibbles.insert(all_nibbles.end(), payload_nibbles.begin(), payload_nibbles.end());

        std::vector<std::uint8_t> raw_bytes;
        raw_bytes.reserve(all_nibbles.size() / 2);
        for (std::size_t i = 0; i + 1 < all_nibbles.size(); i += 2) {
            const std::uint8_t lo = all_nibbles[i] & 0x0F;
            const std::uint8_t hi = all_nibbles[i + 1] & 0x0F;
            raw_bytes.push_back(static_cast<std::uint8_t>((hi << 4) | lo));
        }

        out = PayloadEvent{};
        out.length       = std::min<std::size_t>(payload_length_bytes_, raw_bytes.size());
        out.has_crc      = payload_has_crc_;
        out.sample_index = payload_first_sample_;

        out.raw_symbol_count = std::min<std::size_t>(payload_symbols_.size(),
            sizeof(out.raw_symbols) / sizeof(out.raw_symbols[0]));
        for (std::size_t i = 0; i < out.raw_symbol_count; ++i) {
            out.raw_symbols[i] = static_cast<std::uint16_t>(payload_symbols_[i]);
        }
        out.raw_byte_count = std::min<std::size_t>(raw_bytes.size(),
            sizeof(out.raw_bytes) / sizeof(out.raw_bytes[0]));
        for (std::size_t i = 0; i < out.raw_byte_count; ++i) {
            out.raw_bytes[i] = raw_bytes[i];
        }

        // Dewhiten the data bytes *and* the trailing 2-byte CRC: the TX (and
        // SDRangel) whiten the entire stream, CRC included.
        std::size_t dewhiten_len = out.length;
        if (out.has_crc && raw_bytes.size() >= out.length + 2)
            dewhiten_len = out.length + 2;
        if (dewhiten_len > 0) {
            dewhiten_payload_bytes(
                std::span<std::uint8_t>(raw_bytes.data(), dewhiten_len));
        }
        for (std::size_t i = 0; i < out.length && i < sizeof(out.bytes); ++i) {
            out.bytes[i] = raw_bytes[i];
        }

        if (out.has_crc && out.length >= 2 && raw_bytes.size() >= out.length + 2) {
            // SDRangel `sx1272DataChecksum` over the `length` data bytes; the
            // computed value is appended little-endian as the trailing 2 bytes.
            const std::uint16_t crc = sx1272_data_checksum(
                std::span<const std::uint8_t>(raw_bytes.data(), out.length));
            out.crc_received = static_cast<std::uint16_t>(
                raw_bytes[out.length] |
                (static_cast<std::uint16_t>(raw_bytes[out.length + 1]) << 8));
            out.crc_computed = crc;
            out.crc_ok = (out.crc_received == out.crc_computed);
        }
        return out.crc_ok;
    };

    // Baseline attempt (no offset). Keep it as the reported result unless a
    // shifted retry actually validates the CRC.
    PayloadEvent ev{};
    bool ok = decode_with_delta(0, ev);
    if (!ok && payload_has_crc_) {
        for (const int delta : {-1, 1}) {
            PayloadEvent cand{};
            if (decode_with_delta(delta, cand)) {
                ev = cand;
                break;
            }
        }
    }

    if (pay_cb_) pay_cb_(ev);
}

} // namespace mrf::modem
