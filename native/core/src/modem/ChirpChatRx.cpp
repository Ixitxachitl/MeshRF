// SPDX-License-Identifier: GPL-3.0-or-later
#include "mrf/modem/ChirpChatRx.h"
#include "mrf/modem/LoraDecoder.h"

#include <algorithm>
#include <array>
#include <cmath>
#include <cstring>
#include <numbers>
#include <stdexcept>

namespace mrf::modem {

namespace {

// Build the reference downchirp at chip rate. downchirp[n] = conj(upchirp[n])
// where upchirp_0[n] = exp(j*pi*(n*(n-1)/N - n)).
//
// We compute the phase as (n*(n-1) mod 2N)/N * pi - pi*n  to avoid catastrophic
// precision loss for large N. (n=4096, n*(n-1) ~ 16e6 — fine for double, but
// we keep it tidy.)
std::vector<std::complex<float>> make_downchirp(int n) {
    std::vector<std::complex<float>> dn(static_cast<std::size_t>(n));
    const double pi = std::numbers::pi;
    for (int k = 0; k < n; ++k) {
        // upchirp phase: pi * (k*(k-1)/N - k)
        long long num = static_cast<long long>(k) * (k - 1);
        long long mod = num % (2LL * n); // bring into [0, 2N)
        if (mod < 0) mod += 2LL * n;
        double phase_up = pi * (static_cast<double>(mod) / n - static_cast<double>(k));
        // Downchirp = conj of upchirp -> negate phase.
        double phase = -phase_up;
        dn[static_cast<std::size_t>(k)] = {static_cast<float>(std::cos(phase)),
                                           static_cast<float>(std::sin(phase))};
    }
    return dn;
}

// Find the bin with the largest |X|^2 in `mags`, plus the dB ratio of that
// peak vs. the mean of the rest. dB ratio is a quick "preamble strength"
// proxy used to gate weak detections.
struct PeakResult { int bin; float peak_db; float frac; };
PeakResult find_peak(std::span<const float> mags) {
    int peak_bin = 0;
    float peak = mags[0];
    double sum = 0.0;
    for (std::size_t i = 0; i < mags.size(); ++i) {
        const float m = mags[i];
        sum += m;
        if (m > peak) {
            peak = m;
            peak_bin = static_cast<int>(i);
        }
    }
    // Parabolic interpolation across the 3 magnitude bins around the peak.
    // Gives sub-bin offset δ ∈ [-0.5, +0.5] for the true peak location.
    // Using sqrt of mag² yields slightly better behavior on Gaussian-shaped
    // sinc lobes; we approximate with magnitude (mags is already |X|²).
    const int N = static_cast<int>(mags.size());
    const float ml = mags[static_cast<std::size_t>((peak_bin - 1 + N) % N)];
    const float mc = mags[static_cast<std::size_t>(peak_bin)];
    const float mr = mags[static_cast<std::size_t>((peak_bin + 1) % N)];
    float frac = 0.0f;
    const float denom = ml - 2.0f * mc + mr;
    if (denom < -1e-12f) {
        frac = 0.5f * (ml - mr) / denom;
        if (frac > 0.5f)  frac = 0.5f;
        if (frac < -0.5f) frac = -0.5f;
    }
    const double rest = sum - peak;
    const double mean_rest = rest / static_cast<double>(std::max<std::size_t>(1, mags.size() - 1));
    if (peak <= 0.0f || mean_rest <= 0.0)
        return {peak_bin, 0.0f, frac};
    return {peak_bin,
            static_cast<float>(10.0 * std::log10(static_cast<double>(peak) / mean_rest)),
            frac};
}

} // namespace

ChirpChatRx::ChirpChatRx(std::uint8_t spreading_factor,
                         std::uint32_t chip_rate_hz,
                         std::uint8_t sync_word)
    : sf_(spreading_factor),
      chip_rate_(chip_rate_hz),
      sync_word_(sync_word),
      n_(static_cast<int>(1ULL << spreading_factor)),
      downchirp_(make_downchirp(static_cast<int>(1ULL << spreading_factor))),
      upchirp_(static_cast<std::size_t>(1ULL << spreading_factor)),
      sym_buf_(static_cast<std::size_t>(1ULL << spreading_factor)),
      fft_buf_(static_cast<std::size_t>(1ULL << spreading_factor)),
      fft_(static_cast<std::size_t>(1ULL << spreading_factor)) {
    if (spreading_factor < 5 || spreading_factor > 12)
        throw std::invalid_argument("ChirpChatRx: spreading_factor out of range");
    if (chip_rate_hz == 0)
        throw std::invalid_argument("ChirpChatRx: chip_rate_hz must be > 0");
    // upchirp = conj(downchirp). Precompute so the SFD-search FFT inner
    // loop is a single complex multiply instead of multiply + conj.
    for (int k = 0; k < n_; ++k) {
        upchirp_[static_cast<std::size_t>(k)] =
            std::conj(downchirp_[static_cast<std::size_t>(k)]);
    }
    // NOTE: No FFT window. SDRangel's actual Meshtastic decoder
    // (MeshtasticDemodSink::getLoRaSymbolVal) uses a RECTANGULAR symbol
    // window for symbol decisions — its source explicitly warns that
    // applying a window makes header symbols drift and CRC checks fail.
    // The Kaiser window belongs to the generic ChirpChat plugin, not
    // Meshtastic. LoRa FFT interpolation is also 1 (no zero-pad). Symbol
    // accuracy in the real decoder comes from STO/CFO estimation, not
    // windowing.
}

void ChirpChatRx::reset() {
    std::fill(sym_buf_.begin(), sym_buf_.end(), std::complex<float>{0.0f, 0.0f});
    sym_pos_ = 0;
    sym_filled_ = 0;
    stride_ = 0;
    sample_index_ = 0;
    symbols_processed_ = 0;
    preambles_detected_ = 0;
    reset_frame_state_();
}

void ChirpChatRx::reset_frame_state_() {
    state_ = State::Hunting;
    sfd_consecutive_ = 0;
    sfd_search_budget_ = 0;
    frame_symbol_count_ = 0;
    tracking_remaining_ = 0;
    tracking_index_ = 0;
    recent_bins_.clear();
    recent_peaks_.clear();
    recent_fracs_.clear();
    last_locked_bin_ = -1;
    header_symbols_.clear();
    chosen_header_start_ = 0;
    chosen_header_delta_ = 0;
    payload_symbols_.clear();
    payload_total_symbols_ = 0;
    payload_length_bytes_ = 0;
    payload_coding_rate_ = 0;
    payload_has_crc_ = false;
    payload_ldro_ = false;
    header_leak_nibbles_.clear();
    cfo_bin_ = 0;
    sfd_down_bin_ = 0;
    cfo_int_ = 0;
    sto_int_ = 0;
    cfo_frac_ = 0.0f;
    nco_phase_ = 0.0;
    nco_phase_inc_ = 0.0;
}

void ChirpChatRx::process(std::span<const std::complex<float>> samples) {
    // Fixed stride: emit a candidate symbol every N samples (i.e. every full
    // symbol period). Sliding-by-1 detection is more sensitive but ~N x more
    // FFTs; coarse stride is the SDRangel default for the steady-state
    // detector and is sufficient for preamble lock since we slide our peak
    // window across many symbols anyway.
    //
    // NOTE: the stride MUST be N here. The preamble detector locks on
    // kPreambleConfirm consecutive FFTs that land on the SAME bin. Because
    // dechirping a repeated upchirp through the fixed downchirp produces a
    // tone whose bin = (symbol_shift + window_offset), evaluating at any
    // spacing other than a full symbol makes the peak bin slide linearly
    // with the window (offset grows N/stride bins per fire) and the
    // "same-bin run" never forms. N-spaced fires give a CONSTANT bin for any
    // arrival phase, so timing alignment is already handled.
    //
    // The first N samples just fill the rolling buffer; from then on every
    // N-sample boundary triggers an FFT.

    for (auto s : samples) {
        // Apply fractional-CFO NCO compensation when active. nco_phase_inc_
        // is set to a non-zero value after a preamble locks; up until then
        // (and after the post-frame reset) it's 0.0 and this is a no-op.
        std::complex<float> sn = s;
        if (nco_phase_inc_ != 0.0) {
            const float ph = static_cast<float>(nco_phase_);
            const std::complex<float> rot(std::cos(ph), std::sin(ph));
            sn = s * rot;
            nco_phase_ += nco_phase_inc_;
            // Wrap to keep precision over long runs.
            constexpr double kTwoPi = 6.283185307179586476925286766559;
            if (nco_phase_ >  kTwoPi) nco_phase_ -= kTwoPi;
            if (nco_phase_ < -kTwoPi) nco_phase_ += kTwoPi;
        }
        sym_buf_[static_cast<std::size_t>(sym_pos_)] = sn;
        sym_pos_ = (sym_pos_ + 1) % n_;
        if (sym_filled_ < n_) ++sym_filled_;
        ++sample_index_;
        ++stride_;
        // Cadence: every N samples normally, but every N/4 while searching for
        // the SFD. The preamble locks on a window that is offset from the true
        // symbol boundary by the sample-timing offset (STO); repeated preamble
        // upchirps are insensitive to that offset, but the short 2.25-symbol
        // SFD down-chirp gets split across the offset window and its peak
        // collapses below the detection threshold. Sliding the SFD search at
        // N/4 finds the aligned window where the down-chirp dechirps to a
        // strong tone. (This is a one-shot peak search, NOT the consecutive-
        // same-bin preamble logic, so finer spacing only helps it.)
        const int fire_at = (state_ == State::SfdSearch)
                                ? (n_ / kSfdSearchOversample)
                                : n_;
        if (sym_filled_ < n_ || stride_ < fire_at) continue;
        stride_ = 0;

        // Dechirp into fft_buf_ in chronological order. The oldest sample is
        // at index sym_pos_ (next write slot), so unroll the rolling buffer.
        for (int k = 0; k < n_; ++k) {
            const int idx = (sym_pos_ + k) % n_;
            fft_buf_[static_cast<std::size_t>(k)] =
                sym_buf_[static_cast<std::size_t>(idx)] *
                downchirp_[static_cast<std::size_t>(k)];
        }
        fft_.forward(std::span<std::complex<float>>(fft_buf_.data(),
                                                    static_cast<std::size_t>(n_)));

        // Magnitude squared.
        std::vector<float> mags(static_cast<std::size_t>(n_));
        for (int k = 0; k < n_; ++k) {
            const auto& c = fft_buf_[static_cast<std::size_t>(k)];
            mags[static_cast<std::size_t>(k)] = c.real() * c.real() + c.imag() * c.imag();
        }
        const auto pk = find_peak(mags);
        emit_symbol_(pk.bin, pk.peak_db, pk.frac, sample_index_ - n_);
    }
}

void ChirpChatRx::emit_symbol_(int peak_bin, float peak_db, float peak_frac,
                               std::uint64_t first_sample_index) {
    ++symbols_processed_;

    // -- SfdSearch: detect the SFD by a power crossover. ----------------
    // SDRangel's ChirpChat sink does NOT use an absolute dB threshold here.
    // At each window it dechirps the rolling buffer two ways: with the
    // preamble (down-chirp) template and with the SFD (up-chirp) template,
    // and compares their peak powers. During the preamble the down-chirp
    // dechirp is strong; once the SFD begins, the up-chirp dechirp overtakes
    // it. The crossover (SFD power exceeds preamble power by > 50%) marks the
    // SFD with sub-symbol accuracy and catches weak frames a fixed threshold
    // would miss.  (preDrop = pre - sfd < 0 && -preDrop/sfd > 0.5.)
    if (state_ == State::SfdSearch) {
        int pre_bin = 0, sfd_bin = 0;
        const float pre_magsq = peak_magsq_(downchirp_, pre_bin);
        const float sfd_magsq = peak_magsq_(upchirp_, sfd_bin);
        sfd_down_bin_ = sfd_bin;
        const float pre_drop  = pre_magsq - sfd_magsq;
        const float drop_ratio = sfd_magsq > 0.0f ? (-pre_drop / sfd_magsq) : 0.0f;
        if (sym_cb_) {
            // Forward as a diagnostic SymbolEvent (negative index_in_frame
            // marks SFD-search candidates). Report the crossover ratio in dB
            // so the existing UI filter still shows the strongest candidates.
            const float ratio_db = (pre_magsq > 0.0f && sfd_magsq > 0.0f)
                ? 10.0f * std::log10(sfd_magsq / pre_magsq) : 0.0f;
            sym_cb_(SymbolEvent{-(sfd_consecutive_ + 1), sfd_bin, ratio_db, first_sample_index});
        }
        if (pre_drop < 0.0f && drop_ratio > 0.5f) {
            ++sfd_consecutive_;
            // Disentangle integer CFO and STO (diagnostic only; see below).
            disentangle_cfo_sto(cfo_bin_, sfd_down_bin_, n_, cfo_int_, sto_int_);
            // Anchor on the FIRST down-chirp of the SFD. We are at the END
            // of SFD#1 here. The SFD is 2.25 down-chirp symbols total, so
            // header[0] starts 1.25 symbols (= 5N/4 samples) from now, and
            // its FFT window completes 2.25 symbols (= 9N/4 samples) from
            // now. Setting stride_ = -(N + N/4) makes the next emit fire
            // at +9N/4 samples (default cadence is N samples per emit).
            // The N/4 SFD slide already locked onto a window aligned to the
            // SFD down-chirp boundary, so no extra STO sample-shift is needed
            // here. (The disentangled sto_int_ is kept only as a diagnostic;
            // it is unreliable because the SFD down-chirp bin measurement is
            // noisy, so it must NOT feed the timing or the symbol value.)
            stride_ = -(n_ + n_ / 4);
            state_ = State::HeaderCapture;
            frame_symbol_count_ = 0;
            header_symbols_.clear();
            header_first_sample_ = first_sample_index;
        } else {
            sfd_consecutive_ = 0;
        }
        --sfd_search_budget_;
        if (sfd_search_budget_ <= 0) {
            // Give up; re-arm preamble hunt.
            state_ = State::Hunting;
            recent_bins_.clear();
            recent_peaks_.clear();
            recent_fracs_.clear();
            last_locked_bin_ = -1;
            nco_phase_inc_ = 0.0;
            nco_phase_     = 0.0;
        }
        return;
    }

    // -- HeaderCapture: collect kHeaderSymbols, then decode. ------------
    if (state_ == State::HeaderCapture) {
        if (++frame_symbol_count_ >= kFrameSymbolMax) {
            reset_frame_state_();
            return;
        }
        // Apply offset correction the way SDRangel's ChirpChat sink does:
        // subtract the PREAMBLE reference bin (the combined CFO+STO offset).
        // A data symbol of value s dechirps to (s + cfo_bin_) mod N, so the
        // true value is (peak_bin - cfo_bin_) mod N. The residual sub-symbol
        // timing only smears energy within the window and was already
        // minimized by the N/4 SFD slide.
        const int corrected =
            ((peak_bin - cfo_bin_) % n_ + n_) % n_;
        if (sym_cb_) {
            sym_cb_(SymbolEvent{static_cast<int>(header_symbols_.size()),
                                corrected, peak_db, first_sample_index});
        }
        header_symbols_.push_back(corrected);
        if (static_cast<int>(header_symbols_.size()) == kHeaderCapture) {
            decode_header_();
            // If the header parity was OK and there is a non-empty payload,
            // continue into PayloadCapture; otherwise re-arm the preamble
            // hunt. (decode_header_ sets payload_total_symbols_ > 0 only on
            // a clean header.)
            if (payload_total_symbols_ > 0) {
                state_ = State::PayloadCapture;
                payload_symbols_.clear();
                payload_first_sample_ = first_sample_index;
                // The header decode used symbols [start, start+8). Any symbols
                // captured beyond that are the FIRST payload symbols — seed
                // them so payload sampling stays time-aligned. They were
                // stored as (peak_bin - cfo_bin_old); the chosen delta shifted
                // cfo_bin_ by -delta, so the payload-consistent value is
                // (stored + chosen_delta) mod N.
                const int first_payload =
                    chosen_header_start_ + kHeaderSymbols;
                for (int i = first_payload;
                     i < static_cast<int>(header_symbols_.size()); ++i) {
                    const int v =
                        ((header_symbols_[static_cast<std::size_t>(i)]
                          + chosen_header_delta_) % n_ + n_) % n_;
                    payload_symbols_.push_back(v);
                }
            } else {
                state_ = State::Hunting;
                recent_bins_.clear();
                recent_peaks_.clear();
                recent_fracs_.clear();
                last_locked_bin_ = -1;
                nco_phase_inc_ = 0.0;
                nco_phase_     = 0.0;
            }
            header_symbols_.clear();
        }
        return;
    }

    // -- PayloadCapture: collect payload_total_symbols_, then decode. ---
    if (state_ == State::PayloadCapture) {
        if (++frame_symbol_count_ >= kFrameSymbolMax) {
            reset_frame_state_();
            return;
        }
        const int corrected =
            ((peak_bin - cfo_bin_) % n_ + n_) % n_;
        if (sym_cb_) {
            // Index payload symbols starting at 8 so the UI can distinguish
            // them from the 8 header symbols (indices 0..7).
            sym_cb_(SymbolEvent{
                kHeaderSymbols + static_cast<int>(payload_symbols_.size()),
                corrected, peak_db, first_sample_index});
        }
        payload_symbols_.push_back(corrected);
        if (static_cast<int>(payload_symbols_.size()) >= payload_total_symbols_) {
            decode_payload_();
            state_ = State::Hunting;
            recent_bins_.clear();
            recent_peaks_.clear();
            recent_fracs_.clear();
            last_locked_bin_ = -1;
            payload_symbols_.clear();
            payload_total_symbols_ = 0;
            nco_phase_inc_ = 0.0;
            nco_phase_     = 0.0;
        }
        return;
    }

    // -- Hunting: rolling preamble detector. ----------------------------
    // Maintain a small rolling window of recent peaks/bins.
    constexpr std::size_t kWindow = 8;
    constexpr int kPreambleConfirm = 4;     // require this many in a row
    constexpr float kMinPeakDb = 4.0f;      // 4 dB above mean = ~2.5x energy

    recent_bins_.push_back(peak_bin);
    recent_peaks_.push_back(peak_db);
    recent_fracs_.push_back(peak_frac);
    if (recent_bins_.size() > kWindow) {
        recent_bins_.pop_front();
        recent_peaks_.pop_front();
        recent_fracs_.pop_front();
    }

    // Look for >= kPreambleConfirm consecutive bins within +/- 1 of each other,
    // each with peak_db >= kMinPeakDb. (A single drift step is allowed because
    // SF=11 over LoRa preambles can show one-bin walk under CFO.)
    if (static_cast<int>(recent_bins_.size()) < kPreambleConfirm) return;

    int run = 1;
    int locked_bin = recent_bins_.back();
    int min_run_required = kPreambleConfirm;
    bool ok = recent_peaks_.back() >= kMinPeakDb;
    for (int i = static_cast<int>(recent_bins_.size()) - 2; i >= 0 && ok; --i) {
        const int diff = std::abs(recent_bins_[static_cast<std::size_t>(i)] - locked_bin);
        if (diff <= 1 && recent_peaks_[static_cast<std::size_t>(i)] >= kMinPeakDb) {
            ++run;
            if (run >= min_run_required) break;
        } else {
            ok = false;
        }
    }

    if (!ok || run < kPreambleConfirm) return;

    // Reject preambles with a CFO larger than +/- BW/2 (the full LoRa
    // unambiguous range): SDRangel's ChirpChat accepts this whole window, so
    // matching it lets us lock the same frames whose hardware LO offset lands
    // beyond +/- BW/4. Beyond +/- N/2 the symbol is aliased and unrecoverable.
    {
        int signed_test = locked_bin;
        if (signed_test > n_ / 2) signed_test -= n_;
        if (std::abs(signed_test) >= n_ / 2) return;
    }

    // Suppress duplicate events from the same lock — only re-emit when the
    // bin shifts substantially or after a quiet period.
    if (last_locked_bin_ >= 0 && std::abs(last_locked_bin_ - locked_bin) <= 1) {
        return;
    }
    last_locked_bin_ = locked_bin;
    ++preambles_detected_;
    cfo_bin_ = locked_bin;

    // Average the parabolic fractional offsets across the confirmed
    // preamble symbols to produce a sub-bin CFO estimate. Each symbol's
    // peak is at bin = locked_bin (+/-1), so we add `(recent_bin - locked_bin)`
    // back into the fraction so it's a signed offset relative to the
    // declared lock bin.
    {
        double frac_sum = 0.0;
        int    frac_n   = 0;
        for (std::size_t i = 0; i < recent_fracs_.size(); ++i) {
            const int   b = recent_bins_[i];
            const float f = recent_fracs_[i];
            const float p = recent_peaks_[i];
            if (p < kMinPeakDb) continue;
            if (std::abs(b - locked_bin) > 1) continue;
            frac_sum += static_cast<double>(b - locked_bin) + f;
            ++frac_n;
        }
        cfo_frac_ = frac_n > 0 ? static_cast<float>(frac_sum / frac_n) : 0.0f;
        if (cfo_frac_ >  0.5f) cfo_frac_ =  0.5f;
        if (cfo_frac_ < -0.5f) cfo_frac_ = -0.5f;

        // Set up the NCO so subsequent samples get rotated by
        // `e^{-j 2π cfo_frac / N · n}`, parking the fractional CFO at zero.
        constexpr double kTwoPi = 6.283185307179586476925286766559;
        nco_phase_inc_ = -kTwoPi * static_cast<double>(cfo_frac_) /
                          static_cast<double>(n_);
        nco_phase_     = 0.0;
    }

    // CFO: bin > N/2 wraps to negative.
    int signed_bin = locked_bin;
    if (signed_bin > n_ / 2) signed_bin -= n_;
    const float cfo = static_cast<float>(signed_bin) *
                      static_cast<float>(chip_rate_) / static_cast<float>(n_);

    if (cb_) {
        cb_(PreambleEvent{
            locked_bin,
            cfo,
            recent_peaks_.back(),
            run,
            first_sample_index,
        });
    }

    // Begin SFD search. The frame after the preamble is: 2 sync-word symbols
    // (upchirps, won't show on upchirp-dechirp FFT), then 2.25 downchirps
    // (the SFD, which DO show as strong peaks on upchirp-dechirp FFT). We
    // wait for two consecutive upchirp peaks then realign timing for the
    // header symbols.
    state_ = State::SfdSearch;
    sfd_consecutive_ = 0;
    sfd_search_budget_ = kSfdSearchMaxSymbols * kSfdSearchOversample;
    tracking_remaining_ = 0;
    tracking_index_ = 0;
    header_symbols_.clear();
}

float ChirpChatRx::upchirp_peak_db_() {
    // Multiply current symbol window by upchirp template, FFT, return peak_db.
    for (int k = 0; k < n_; ++k) {
        const int idx = (sym_pos_ + k) % n_;
        fft_buf_[static_cast<std::size_t>(k)] =
            sym_buf_[static_cast<std::size_t>(idx)] *
            upchirp_[static_cast<std::size_t>(k)];
    }
    fft_.forward(std::span<std::complex<float>>(fft_buf_.data(),
                                                 static_cast<std::size_t>(n_)));
    std::vector<float> mags(static_cast<std::size_t>(n_));
    for (int k = 0; k < n_; ++k) {
        const auto& c = fft_buf_[static_cast<std::size_t>(k)];
        mags[static_cast<std::size_t>(k)] = c.real() * c.real() + c.imag() * c.imag();
    }
    const auto pk = find_peak(mags);
    // Record the SFD down-chirp peak bin for CFO/STO disentanglement.
    sfd_down_bin_ = pk.bin;
    return pk.peak_db;
}

float ChirpChatRx::peak_magsq_(const std::vector<std::complex<float>>& templ,
                               int& out_bin) {
    for (int k = 0; k < n_; ++k) {
        const int idx = (sym_pos_ + k) % n_;
        fft_buf_[static_cast<std::size_t>(k)] =
            sym_buf_[static_cast<std::size_t>(idx)] *
            templ[static_cast<std::size_t>(k)];
    }
    fft_.forward(std::span<std::complex<float>>(fft_buf_.data(),
                                                 static_cast<std::size_t>(n_)));
    float peak = 0.0f;
    int   peak_bin = 0;
    for (int k = 0; k < n_; ++k) {
        const auto& c = fft_buf_[static_cast<std::size_t>(k)];
        const float m = c.real() * c.real() + c.imag() * c.imag();
        if (m > peak) { peak = m; peak_bin = k; }
    }
    out_bin = peak_bin;
    return peak;
}

void ChirpChatRx::disentangle_cfo_sto(int up_bin, int down_bin, int n,
                                      int& cfo_int, int& sto_int) {
    // Fold each raw bin into the signed range [-N/2, N/2).
    auto fold = [n](int b) {
        b %= n;
        if (b < 0) b += n;
        if (b > n / 2) b -= n;
        return b;
    };
    const int su = fold(up_bin);
    const int sd = fold(down_bin);
    // up_bin = (CFO - STO), down_bin = (CFO + STO):
    //   CFO = (up + down)/2, STO = (down - up)/2.
    // (su+sd) and (sd-su) always share parity; round-half-to-even is
    // irrelevant here — std::lround rounds half away from zero, which is fine
    // for a +/-0.5-bin ambiguity that the fractional-CFO loop absorbs anyway.
    cfo_int = static_cast<int>(std::lround((su + sd) / 2.0));
    sto_int = static_cast<int>(std::lround((sd - su) / 2.0));
    // Fold CFO back into signed range (sum could exceed N/2).
    cfo_int = fold(cfo_int);
}

void ChirpChatRx::retrack_cfo_(float peak_frac, float peak_db) {
    // Gate: weak symbols give noisy parabolic estimates that would jitter
    // the loop. Skip retrack for any symbol below a strong-peak threshold.
    if (peak_db < 12.0f) return;
    // peak_frac is the parabolic sub-bin offset of THIS symbol's FFT peak
    // from its integer bin. After preamble lock, residual CFO drift shows
    // up here as a non-zero average frac. Nudge nco_phase_inc_ by a small
    // fraction so drift is absorbed continuously without overreacting to
    // single noisy symbols.
    //
    // We run the modem at the chip rate, so a frac of +1 bin corresponds
    // to a phase-increment delta of -2π/N per sample.
    if (!std::isfinite(peak_frac)) return;
    if (peak_frac >  0.5f) peak_frac =  0.5f;
    if (peak_frac < -0.5f) peak_frac = -0.5f;
    constexpr double kTwoPi = 6.283185307179586476925286766559;
    constexpr double kAlpha = 0.03; // EMA gain — small to ride out per-symbol noise
    const double delta_inc = -kTwoPi * static_cast<double>(peak_frac) /
                              static_cast<double>(n_);
    nco_phase_inc_ += kAlpha * delta_inc;
}

void ChirpChatRx::decode_header_() {
    using namespace mrf::modem::lora;
    // Verbatim port of SDRangel's `chirpchatdemoddecoderlora.cpp::decodeHeader`
    // semantics (which itself ports LoRa-SDR `LoRaCodes.hpp`):
    //   1. evalSymbol + binaryToGray16 on each of the 8 header symbols
    //   2. diagonalDeterleaveSx with PPM=sf_app=sf-2, RDD=4 -> sf_app codewords
    //   3. decodeHamming84sx on the first 5 codewords -> 5 nibbles
    //   4. parse {length, fec_info, checksum} per the canonical layout
    //   5. checksum is OK iff (got_checksum ^ headerChecksum(length, fec_info)) == 0
    //
    // SDRangel's tryHeaderLock: the integer CFO recovered at the SFD can be a
    // bin or two off from the value that aligns the header symbols (the SFD
    // and preamble peaks are measured in slightly different sample windows).
    // So we try a small range of constant symbol offsets (deltas) and keep
    // the first one whose header checksum validates. delta=0 is tried first,
    // so a clean header is unaffected. This is deterministic — no guessing.
    const std::uint8_t sf_app = static_cast<std::uint8_t>(sf_ - 2);
    const std::uint8_t cr_app = 8;

    // Order of deltas to try: 0 first (clean header unaffected), then a
    // symmetric outward sweep. The preamble reference bin and the header
    // sampling window are measured in different parts of the frame, so the
    // residual integer offset can be several bins; sweep wide enough to
    // cover it. Still deterministic (validated by header CRC), no guessing.
    static constexpr int kHeaderDeltas[] = {
        0, -1, 1, -2, 2, -3, 3, -4, 4, -5, 5, -6, 6, -7, 7, -8, 8};

    HeaderEvent ev{};
    std::vector<std::uint8_t> cws; // codewords of the accepted (or last) try
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
        const std::uint8_t length   = static_cast<std::uint8_t>(
            ((out.raw_nibbles[0] & 0x0F) << 4) | (out.raw_nibbles[1] & 0x0F));
        const std::uint8_t fec_info = static_cast<std::uint8_t>(out.raw_nibbles[2] & 0x0F);
        const std::uint8_t got_chk  = static_cast<std::uint8_t>(
            ((out.raw_nibbles[3] & 0x0F) << 4) | (out.raw_nibbles[4] & 0x0F));
        const std::uint8_t expected = header_crc5(length, fec_info);
        out.payload_length = length;
        out.has_crc     = (fec_info & 0x01) != 0;
        out.coding_rate = static_cast<std::uint8_t>((fec_info >> 1) & 0x07);
        out.parity_ok   = (got_chk == expected);
        // The header CRC is only 5 bits, so a wide (start × delta) search
        // would otherwise accept ~1-in-32 garbage candidates. Add structural
        // sanity gates that every REAL Meshtastic frame satisfies but random
        // bit patterns rarely do, cutting the false-lock rate sharply:
        //   * coding_rate must be a valid 4/5..4/8 code (1..4).
        //   * has_crc must be set — Meshtastic always enables the LoRa CRC.
        //   * payload_length must be >= 16 (the PacketHeader is 16 bytes) and
        //     <= the LoRa max (255).
        // The payload CRC-16 (1-in-65536) remains the final arbiter downstream.
        const bool sane =
            out.parity_ok &&
            out.has_crc &&
            out.coding_rate >= 1 && out.coding_rate <= 4 &&
            length >= 16 && length <= 255;
        return sane;
    };

    bool locked = false;
    // Outer loop: anchor start offset (timing search). The SFD crossover can
    // land a symbol or two early, so the true header may begin a few symbols
    // into the capture. Inner loop: bin-value delta (frequency search). Try
    // start=0 / delta=0 first so a cleanly-anchored header is unaffected.
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
            // Keep the start=0 / delta=0 result as the reported failure.
            if (start == 0 && delta == 0) { ev = cand; cws = cand_cws; }
        }
    }
    chosen_header_start_ = chosen_start;
    chosen_header_delta_ = locked ? chosen_delta : 0;
    // Apply the chosen delta to the reference bin so the payload path
    // (which subtracts cfo_bin_) inherits the same correction.
    if (locked && chosen_delta != 0) {
        cfo_bin_ = ((cfo_bin_ - chosen_delta) % n_ + n_) % n_;
    }

    // Default: no payload to capture unless a valid header sets it below.
    payload_total_symbols_ = 0;
    header_leak_nibbles_.clear();

    if (locked && ev.parity_ok && cws.size() >= 5) {
        // Header fields (payload_length / has_crc / coding_rate / parity_ok)
        // were already parsed inside try_delta for the accepted delta.
        // Set up payload-capture parameters when the header validates.
        // Number-of-payload-symbols formula (LoRa explicit header):
        //   N = max(0, ceil((8*PL - 4*SF + 28 + 16*CRC - 20*IH) /
        //                   (4*(SF - 2*LDRO)))) * (CR + 4)
        // IH=0 (we only support explicit). LDRO is on when symbol time is
        // >= 16 ms; that's a config-time decision but the closest proxy we
        // have is `n_ / chip_rate_ >= 16 ms`. Round to the nearest float ms.
        if (ev.coding_rate >= 1 && ev.coding_rate <= 4) {
            // LDRO is enabled when symbol time T_sym = 2^SF / BW >= 16 ms
            // (per Semtech AN1200.13). For Meshtastic configs (BW=125kHz
            // up SF=11/12 only), this is true at SF >= 11. Below that,
            // payload uses PPM=SF (no reduction); above, PPM=SF-2.
            const double t_sym_ms =
                1000.0 * static_cast<double>(n_) /
                static_cast<double>(chip_rate_);
            payload_ldro_ = (t_sym_ms >= 16.0);
            const int sf  = static_cast<int>(sf_);
            const int eff = sf - (payload_ldro_ ? 2 : 0); // = ppm
            const int pl  = static_cast<int>(ev.payload_length);
            const int crc = ev.has_crc ? 1 : 0;
            const int cr  = static_cast<int>(ev.coding_rate);
            const int num = 8 * pl - 4 * sf + 28 + 16 * crc; // numerator
            const int den = 4 * eff;                          // denominator
            int blocks = (num + den - 1) / den;               // ceil
            if (blocks < 0) blocks = 0;
            payload_total_symbols_   = blocks * (cr + 4);
            payload_length_bytes_    = ev.payload_length;
            payload_coding_rate_     = ev.coding_rate;
            payload_has_crc_         = ev.has_crc;

            // Extract the (sf_app - 5) "leaked" payload nibbles from the
            // header section. Per SDRangel's Meshtastic decoder these are
            // *not* whitened at the codeword level; whitening happens at
            // the byte level after Hamming decode, on the data portion
            // only (excluding header bytes and CRC bytes).
            header_leak_nibbles_.clear();
            if (cws.size() > 5) {
                for (std::size_t k = 5; k < cws.size(); ++k) {
                    bool corr = false;
                    header_leak_nibbles_.push_back(
                        hamming_decode(cws[k], 4, corr) & 0x0F);
                }
            }
        } else {
            payload_total_symbols_ = 0;
            header_leak_nibbles_.clear();
        }
    }

    // Diagnostics: expose the header symbols actually used (from the chosen
    // anchor start) and recovered sync parameters so a failed header can be
    // analyzed offline.
    for (int i = 0; i < kHeaderSymbols; ++i) {
        ev.raw_symbols[i] = static_cast<std::uint16_t>(
            header_symbols_[static_cast<std::size_t>(chosen_start + i)]);
    }
    ev.cfo_int      = cfo_int_;
    ev.sto_int      = sto_int_;
    ev.chosen_delta = locked ? chosen_delta : 0;
    ev.chosen_start = locked ? chosen_start : 0;

    if (hdr_cb_) hdr_cb_(ev);
}

void ChirpChatRx::decode_payload_() {
    using namespace mrf::modem::lora;

    // Effective bits per symbol after Gray demap. For payload (no LDRO):
    // ppm = SF; with LDRO:  ppm = SF - 2.
    const std::uint8_t ppm    = static_cast<std::uint8_t>(sf_ - (payload_ldro_ ? 2 : 0));
    const std::uint8_t cr     = payload_coding_rate_;        // 1..4
    const std::uint8_t cw_len = static_cast<std::uint8_t>(cr + 4);
    if (payload_symbols_.size() < static_cast<std::size_t>(payload_total_symbols_) ||
        cr < 1 || cr > 4 || ppm == 0) {
        return;
    }

    // 1. Gray-demap each captured symbol. gr-lora_sdr / SDRangel apply
    //    a `(sym - 1) mod N` correction first (LoRa transmits sym+1; the
    //    receiver undoes that). Header path already folds this into
    //    `symbol_to_bits` via `(sym+1)/4`; here we do the explicit
    //    subtract-1 for the payload, then plain Gray, then drop low bits
    //    when LDRO is on (handled inside `gray_demap` via sf-ppm).
    const int n_total = 1 << sf_;
    std::vector<std::uint16_t> sym_bits(payload_symbols_.size());
    for (std::size_t i = 0; i < payload_symbols_.size(); ++i) {
        const int raw = static_cast<int>(payload_symbols_[i]);
        const std::uint16_t corr = static_cast<std::uint16_t>(
            ((raw - 1) % n_total + n_total) % n_total);
        sym_bits[i] = gray_demap(corr, sf_, ppm);
    }

    // 2. Deinterleave block-by-block: each block is `cw_len` symbols ->
    //    `ppm` codewords of `cw_len` bits.
    std::vector<std::uint8_t> codewords;
    codewords.reserve(static_cast<std::size_t>(payload_total_symbols_) / cw_len * ppm);
    const std::size_t total_blocks =
        static_cast<std::size_t>(payload_total_symbols_) / cw_len;
    for (std::size_t b = 0; b < total_blocks; ++b) {
        const std::span<const std::uint16_t> block(
            sym_bits.data() + b * cw_len, cw_len);
        auto cws = deinterleave(block, ppm, cw_len);
        for (auto cw : cws) codewords.push_back(cw);
    }

    // 3. Hamming-decode each codeword into a 4-bit nibble. (No codeword-
    //    level whitening: SDRangel's Meshtastic decoder dewhitens at the
    //    byte level after FEC, on the data portion only.)
    std::vector<std::uint8_t> payload_nibbles;
    payload_nibbles.reserve(codewords.size());
    for (auto cw : codewords) {
        bool corr = false;
        payload_nibbles.push_back(hamming_decode(cw, cr, corr) & 0x0F);
    }

    // 5. Concatenate header-leak nibbles with payload-section nibbles
    //    and pack low-nibble-first into bytes. This buffer corresponds to
    //    bytes[3..] of SDRangel's `bytes` array: it holds `length` data
    //    bytes followed by 2 CRC bytes (when has_crc).
    std::vector<std::uint8_t> all_nibbles;
    all_nibbles.reserve(header_leak_nibbles_.size() + payload_nibbles.size());
    all_nibbles.insert(all_nibbles.end(),
        header_leak_nibbles_.begin(), header_leak_nibbles_.end());
    all_nibbles.insert(all_nibbles.end(),
        payload_nibbles.begin(), payload_nibbles.end());

    std::vector<std::uint8_t> raw_bytes;
    raw_bytes.reserve(all_nibbles.size() / 2);
    for (std::size_t i = 0; i + 1 < all_nibbles.size(); i += 2) {
        const std::uint8_t lo = all_nibbles[i] & 0x0F;
        const std::uint8_t hi = all_nibbles[i + 1] & 0x0F;
        raw_bytes.push_back(static_cast<std::uint8_t>((hi << 4) | lo));
    }

    // 6. Dewhiten the payload bytes (XOR with PN9 LUT). This matches
    //    SDRangel's `dewhitenPayloadBytes(bytes.data() + 3, packetLength)`:
    //    only the `length` data bytes are dewhitened; the trailing CRC
    //    bytes stay raw.
    PayloadEvent ev{};
    ev.length        = std::min<std::size_t>(payload_length_bytes_, raw_bytes.size());
    ev.has_crc       = payload_has_crc_;
    ev.sample_index  = payload_first_sample_;

    // Diagnostics: capture raw symbols and pre-dewhiten bytes BEFORE we
    // mutate raw_bytes. Truncate to the static-array capacity.
    ev.raw_symbol_count = std::min<std::size_t>(payload_symbols_.size(),
        sizeof(ev.raw_symbols) / sizeof(ev.raw_symbols[0]));
    for (std::size_t i = 0; i < ev.raw_symbol_count; ++i) {
        ev.raw_symbols[i] = static_cast<std::uint16_t>(payload_symbols_[i]);
    }
    ev.raw_byte_count = std::min<std::size_t>(raw_bytes.size(),
        sizeof(ev.raw_bytes) / sizeof(ev.raw_bytes[0]));
    for (std::size_t i = 0; i < ev.raw_byte_count; ++i) {
        ev.raw_bytes[i] = raw_bytes[i];
    }

    // Dewhiten the data bytes *and* the trailing 2-byte CRC: the TX (and
    // SDRangel) whiten the entire stream, CRC included.
    std::size_t dewhiten_len = ev.length;
    if (ev.has_crc && raw_bytes.size() >= ev.length + 2)
        dewhiten_len = ev.length + 2;
    if (dewhiten_len > 0) {
        dewhiten_payload_bytes(
            std::span<std::uint8_t>(raw_bytes.data(), dewhiten_len));
    }
    for (std::size_t i = 0; i < ev.length && i < sizeof(ev.bytes); ++i) {
        ev.bytes[i] = raw_bytes[i];
    }

    // 7. Compute and verify the SDRangel `sx1272DataChecksum` over the
    //    `length` data bytes; the value is appended little-endian as the two
    //    trailing CRC bytes (whitened with the rest of the stream).
    if (ev.has_crc && ev.length >= 2 && raw_bytes.size() >= ev.length + 2) {
        const std::uint16_t crc = sx1272_data_checksum(
            std::span<const std::uint8_t>(raw_bytes.data(), ev.length));
        ev.crc_received = static_cast<std::uint16_t>(
            raw_bytes[ev.length] |
            (static_cast<std::uint16_t>(raw_bytes[ev.length + 1]) << 8));
        ev.crc_computed = crc;
        ev.crc_ok = (ev.crc_received == ev.crc_computed);
    }

    if (pay_cb_) pay_cb_(ev);
}

} // namespace mrf::modem
