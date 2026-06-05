// SPDX-License-Identifier: GPL-3.0-or-later
//
// LoRa modem facade. Drives a `ChirpChatRx` for the receive chain (dechirp,
// FFT, preamble detection); header / payload decode + the matching TX
// modulator are still TODO.

#include "mrf/modem/LoraModem.h"
#include "mrf/modem/MeshtasticRx.h"
#include "mrf/modem/LoraEncoder.h"
#include "mrf/modem/ChirpChatTx.h"

#include <cstdio>
#include <cstring>
#include <stdexcept>
#include <utility>

namespace mrf::modem {
namespace {

// Samples per chip fed to the receiver. Oversampling >= 2 is required so the
// fractional-STO sample shift has resolution and integer CFO can be realigned
// during sync; at OS = 1 timing never locks. OS = 4 matches gr-lora_sdr.
constexpr std::uint32_t kOversampling = 4;

class ChirpChatModem final : public ILoraModem {
public:
    explicit ChirpChatModem(const LoraParams& p)
        : params_(p),
          rx_(p.spreading_factor, p.bandwidth_hz,
              static_cast<int>(kOversampling), p.sync_word) {
        rx_.set_event_callback([this](const PreambleEvent& ev) {
            if (!event_cb_) return;
            char msg[160];
            std::snprintf(msg, sizeof(msg),
                "preamble: SF%u BW%uk cfo=%+.1fk peak=%.1fdB",
                static_cast<unsigned>(params_.spreading_factor),
                static_cast<unsigned>(params_.bandwidth_hz / 1000u),
                ev.cfo_hz / 1000.0f,
                ev.peak_db);
            event_cb_(std::string(msg));
        });
        // Per-symbol logging is diagnostic-only; leave the callback unset to
        // keep the log to ~3 lines per frame (preamble / header / payload).
        // SFD-search diagnostic: ChirpChatRx forwards SFD-search candidates
        // with a NEGATIVE index_in_frame (= -(consecutive+1)) and the
        // up-chirp-dechirp peak strength in peak_db. Logging just these tells
        // us, when a preamble locks but no header follows, whether the SFD
        // down-chirp is being seen at all and at what dB (vs. the 12 dB lock
        // threshold). Header/payload symbols (index >= 0) are NOT logged here
        // to keep the log readable.
        rx_.set_symbol_callback([this](const SymbolEvent& ev) {
            if (!event_cb_) return;
            if (ev.index_in_frame >= 0) return; // only SFD-search candidates
            // peak_db is now the SFD/preamble power crossover RATIO in dB.
            // The crossover fires at > ~3 dB (SFD power > 2x preamble). Show
            // candidates climbing toward that so the alignment is visible
            // without flooding the log with the many N/4 sub-windows.
            if (ev.peak_db < 2.0f) return;
            char msg[96];
            std::snprintf(msg, sizeof(msg),
                "  sfd? bin=%d ratio=%.1fdB", ev.symbol_value, ev.peak_db);
            event_cb_(std::string(msg));
        });
        rx_.set_header_callback([this](const HeaderEvent& ev) {
            if (!event_cb_) return;
            char msg[160];
            if (ev.parity_ok) {
                std::snprintf(msg, sizeof(msg),
                    "  header[OK] len=%u cr=4/%u crc=%s",
                    static_cast<unsigned>(ev.payload_length),
                    static_cast<unsigned>(4 + ev.coding_rate),
                    ev.has_crc ? "on" : "off");
                event_cb_(std::string(msg));
                // Also emit the sync internals for a good frame so they can be
                // compared against failing frames.
                char okdbg[160];
                std::snprintf(okdbg, sizeof(okdbg),
                    "    sync cfo=%d k=%d cf=%.3f sf=%.3f dv=%d au=%d d=%d s=%d",
                    ev.cfo_int, ev.k_hat, ev.cfo_frac, ev.sto_frac,
                    ev.down_val, ev.add_upchirps, ev.chosen_delta, ev.chosen_start);
                event_cb_(std::string(okdbg));
                return;
            } else {
                // Show the raw nibbles when parity fails so we can debug.
                char nib[24] = {0};
                std::size_t off = 0;
                for (std::size_t i = 0; i < std::min<std::size_t>(ev.nibble_count, 5) && off + 2 < sizeof(nib); ++i) {
                    off += std::snprintf(nib + off, sizeof(nib) - off, "%X", ev.raw_nibbles[i]);
                }
                std::snprintf(msg, sizeof(msg),
                    "  header[BAD] nibbles=%s", nib);
                event_cb_(std::string(msg));
                // Dump the raw header symbol bins + recovered sync params so
                // the misalignment can be analyzed without live re-runs.
                char dbg[224];
                std::snprintf(dbg, sizeof(dbg),
                    "    hsym=%u,%u,%u,%u,%u,%u,%u,%u cfo=%d k=%d cf=%.3f sf=%.3f dv=%d au=%d d=%d s=%d",
                    static_cast<unsigned>(ev.raw_symbols[0]),
                    static_cast<unsigned>(ev.raw_symbols[1]),
                    static_cast<unsigned>(ev.raw_symbols[2]),
                    static_cast<unsigned>(ev.raw_symbols[3]),
                    static_cast<unsigned>(ev.raw_symbols[4]),
                    static_cast<unsigned>(ev.raw_symbols[5]),
                    static_cast<unsigned>(ev.raw_symbols[6]),
                    static_cast<unsigned>(ev.raw_symbols[7]),
                    ev.cfo_int, ev.k_hat, ev.cfo_frac, ev.sto_frac,
                    ev.down_val, ev.add_upchirps, ev.chosen_delta, ev.chosen_start);
                event_cb_(std::string(dbg));
                return;
            }
        });
        rx_.set_payload_callback([this](const PayloadEvent& ev) {
            if (!event_cb_) return;
            // Full payload as contiguous hex so the app can record the entire
            // decoded frame (not just a preview). Meshtastic frames are short
            // (<= a few hundred bytes), so building a std::string is cheap.
            std::string hex;
            hex.reserve(ev.length * 2);
            char byte_hex[3];
            for (std::size_t i = 0; i < ev.length; ++i) {
                std::snprintf(byte_hex, sizeof(byte_hex), "%02X", ev.bytes[i]);
                hex += byte_hex;
            }
            char head[96];
            std::string msg;
            if (ev.has_crc) {
                std::snprintf(head, sizeof(head),
                    "  payload[%s] len=%zu crc=%04X/%04X ",
                    ev.crc_ok ? "OK" : "BAD",
                    ev.length,
                    static_cast<unsigned>(ev.crc_received),
                    static_cast<unsigned>(ev.crc_computed));
                msg = std::string(head) + hex;
            } else {
                std::snprintf(head, sizeof(head),
                    "  payload len=%zu ", ev.length);
                msg = std::string(head) + hex;
            }
            event_cb_(msg);

            // -- Diagnostic dumps when CRC fails --------------------------
            // Emit the raw payload symbols and pre-dewhiten bytes so they
            // can be cross-checked against gr-lora_sdr / SDRangel offline.
            if (ev.has_crc && !ev.crc_ok) {
                {
                    char buf[8 * 64 + 32] = "    sym=";
                    std::size_t o = std::strlen(buf);
                    for (std::size_t i = 0; i < ev.raw_symbol_count && o + 6 < sizeof(buf); ++i) {
                        o += std::snprintf(buf + o, sizeof(buf) - o, "%u%s",
                            static_cast<unsigned>(ev.raw_symbols[i]),
                            (i + 1 < ev.raw_symbol_count) ? "," : "");
                    }
                    event_cb_(std::string(buf));
                }
                {
                    char buf[3 * 260 + 32] = "    raw=";
                    std::size_t o = std::strlen(buf);
                    for (std::size_t i = 0; i < ev.raw_byte_count && o + 3 < sizeof(buf); ++i) {
                        o += std::snprintf(buf + o, sizeof(buf) - o, "%02X", ev.raw_bytes[i]);
                    }
                    event_cb_(std::string(buf));
                }
            }
        });
    }

    void process_rx(std::span<const Sample> samples) override {
        rx_.process(samples);
    }

    std::vector<Sample> encode(std::span<const std::uint8_t> payload) const override {
        // Modulate the on-air bytes (16-byte L1 header + encrypted payload)
        // into a complete IQ frame at the modem's working sample rate. The
        // PHY chain (CRC, whitening, Hamming, interleave, Gray) is the exact
        // inverse of the receiver in MeshtasticRx; the chirp synthesis in
        // ChirpChatTx matches what the RX demodulates.
        if (payload.empty()) return {};

        // Low-data-rate optimize: enabled when one symbol is >= 16 ms, the
        // same rule MeshtasticRx applies on decode.
        const double t_sym_ms =
            1000.0 * static_cast<double>(1u << params_.spreading_factor) /
            static_cast<double>(params_.bandwidth_hz);
        const bool ldro = params_.low_data_rate_optimize || (t_sym_ms >= 16.0);
        const std::uint8_t cr = static_cast<std::uint8_t>(params_.coding_rate - 4); // 4/5..4/8 -> 1..4

        const auto symbols = lora::encode_frame_symbols(
            payload, params_.spreading_factor, cr, params_.crc_enabled, ldro);

        ChirpChatTx tx(params_.spreading_factor, params_.bandwidth_hz,
                       static_cast<int>(kOversampling), params_.sync_word,
                       params_.preamble_symbols);
        return tx.modulate(
            std::span<const std::uint16_t>(symbols.data(), symbols.size()));
    }

    void set_frame_callback(FrameCallback cb) override { frame_cb_ = std::move(cb); }
    void set_event_callback(EventCallback cb) override { event_cb_ = std::move(cb); }

    const LoraParams& params() const override { return params_; }
    std::uint32_t working_sample_rate_hz() const override {
        // Run the receiver oversampled at kOversampling samples per chip. The
        // Resampler in Core decimates the radio rate down to this directly.
        return params_.bandwidth_hz * kOversampling;
    }

private:
    LoraParams    params_;
    MeshtasticRx  rx_;
    FrameCallback frame_cb_;
    EventCallback event_cb_;
};

} // namespace

std::unique_ptr<ILoraModem> make_modem(const LoraParams& params) {
    if (params.spreading_factor < 5 || params.spreading_factor > 12)
        throw std::invalid_argument("spreading_factor out of range");
    if (params.coding_rate < 5 || params.coding_rate > 8)
        throw std::invalid_argument("coding_rate out of range");
    return std::make_unique<ChirpChatModem>(params);
}

} // namespace mrf::modem
