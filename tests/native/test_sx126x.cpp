// SPDX-License-Identifier: GPL-3.0-or-later
//
// Unit tests for the parts of the SX1262 transmit path that do not need the
// hardware: PHY parameter translation, airtime, and the per-board power model.
// The SPI conversation itself is only exercised against a real stick.
#include "../../native/core/src/hal/Sx126x.h"

#include <gtest/gtest.h>

using namespace mrf;
using mrf::hal::Sx126xBoard;

namespace {

modem::LoraParams params(std::uint8_t sf, std::uint32_t bw, std::uint8_t cr,
                         bool ldro = false) {
    modem::LoraParams p{};
    p.spreading_factor = sf;
    p.bandwidth_hz = bw;
    p.coding_rate = cr;
    p.sync_word = 0x2B;
    p.preamble_symbols = 16;
    p.explicit_header = true;
    p.crc_enabled = true;
    p.low_data_rate_optimize = ldro;
    return p;
}

} // namespace

TEST(Sx126xBandwidth, MapsEveryMeshtasticPresetBandwidth) {
    EXPECT_EQ(hal::sx126x_bandwidth_code(500'000), 0x06);
    EXPECT_EQ(hal::sx126x_bandwidth_code(250'000), 0x05);
    EXPECT_EQ(hal::sx126x_bandwidth_code(125'000), 0x04);
    EXPECT_EQ(hal::sx126x_bandwidth_code(62'500), 0x03);
    EXPECT_EQ(hal::sx126x_bandwidth_code(31'250), 0x02);
}

TEST(Sx126xBandwidth, SnapsMeshRfsRoundedTinyBandwidthToTheChipStep) {
    // MeshRF's TinyFast/TinySlow presets say 15600 Hz; the chip's nearest step
    // is 15630. Nearest-match keeps those presets usable rather than silently
    // falling back to a default.
    EXPECT_EQ(hal::sx126x_bandwidth_code(15'600), 0x01);
}

TEST(Sx126xSyncWord, EncodesMeshtasticAndLoRaWanWords) {
    std::uint8_t msb = 0, lsb = 0;
    hal::sx126x_sync_word_bytes(0x2B, msb, lsb);
    EXPECT_EQ(msb, 0x24);
    EXPECT_EQ(lsb, 0xB4);

    // The public LoRaWAN word is the well-documented cross-check: 0x34 has to
    // land on 0x3444.
    hal::sx126x_sync_word_bytes(0x34, msb, lsb);
    EXPECT_EQ(msb, 0x34);
    EXPECT_EQ(lsb, 0x44);
}

TEST(Sx126xPower, MeshStickRadiatesWhatTheChipProduces) {
    const auto& p = hal::sx126x_profile(Sx126xBoard::MeshStick);
    EXPECT_EQ(hal::sx126x_chip_power_dbm(p, 22), 22);
    EXPECT_EQ(hal::sx126x_chip_power_dbm(p, 0), 0);
    EXPECT_EQ(hal::sx126x_chip_power_dbm(p, -9), -9);
}

TEST(Sx126xPower, MeshToadSubtractsItsExternalPaGain) {
    const auto& p = hal::sx126x_profile(Sx126xBoard::MeshToad);
    // 30 dBm at the antenna is 22 dBm out of the chip through the E22P's PA.
    EXPECT_EQ(hal::sx126x_chip_power_dbm(p, 30), 22);
    EXPECT_EQ(hal::sx126x_chip_power_dbm(p, 20), 12);
}

TEST(Sx126xPower, ClampsAboveAndBelowWhatTheChipAccepts) {
    const auto& stick = hal::sx126x_profile(Sx126xBoard::MeshStick);
    // Asking for more than the SX1262 can make must not wrap the signed byte
    // written to SetTxParams.
    EXPECT_EQ(hal::sx126x_chip_power_dbm(stick, 40), 22);
    EXPECT_EQ(hal::sx126x_chip_power_dbm(stick, -40), -9);

    const auto& toad = hal::sx126x_profile(Sx126xBoard::MeshToad);
    EXPECT_EQ(hal::sx126x_chip_power_dbm(toad, 127), 22);
}

TEST(Sx126xPower, NoBoardCanEverOverdriveTheChip) {
    // The safety property behind letting the user pick the board by hand: a
    // wrong choice must not be able to ask the radio for more than it can
    // legally produce. SetTxParams takes a signed byte and the SX1262's
    // maximum is +22 dBm, so every profile, at every requested level, has to
    // land inside [-9, 22] — including the MeshToad, whose external PA is
    // itself specified for a 22 dBm drive.
    for (auto board : {Sx126xBoard::MeshStick, Sx126xBoard::MeshToad,
                       Sx126xBoard::Unspecified}) {
        const auto& p = hal::sx126x_profile(board);
        for (int requested = -128; requested <= 127; ++requested) {
            const std::int8_t chip =
                hal::sx126x_chip_power_dbm(p, static_cast<std::int8_t>(requested));
            EXPECT_GE(chip, -9) << "board " << p.name << " request " << requested;
            EXPECT_LE(chip, 22) << "board " << p.name << " request " << requested;
        }
    }
}

TEST(Sx126xPower, MismatchedBoardMisreportsButNeverOverdrives) {
    const auto& stick = hal::sx126x_profile(Sx126xBoard::MeshStick);
    const auto& toad  = hal::sx126x_profile(Sx126xBoard::MeshToad);

    // Wrong way round #1 — MeshStick hardware, MeshToad selected. Asking for
    // the MeshToad's 30 dBm still programs only 22, so the stick radiates 22
    // and the UI merely over-states. The harmless direction.
    EXPECT_EQ(hal::sx126x_chip_power_dbm(toad, 30), 22);

    // Wrong way round #2 — MeshToad hardware, MeshStick selected. 22 dBm asked
    // for, 22 dBm programmed, but the real board's PA turns that into ~30 dBm
    // at the antenna. Nothing is overdriven, yet the user is transmitting 8 dB
    // hotter than the UI claims. This is the direction that matters, and the
    // reason the board is logged in full on every open.
    EXPECT_EQ(hal::sx126x_chip_power_dbm(stick, 22), 22);
    EXPECT_EQ(hal::sx126x_chip_power_dbm(toad, 22), 14);
}

TEST(Sx126xProfiles, UnspecifiedCannotTransmitAndIsTheSafeFallback) {
    const auto& none = hal::sx126x_profile(Sx126xBoard::Unspecified);
    EXPECT_EQ(none.min_out_dbm, 0);
    EXPECT_EQ(none.max_out_dbm, 0);

    // An enum value from a corrupt setting or a newer build must land here,
    // not on a real board whose power model nobody chose.
    const auto& bogus = hal::sx126x_profile(static_cast<Sx126xBoard>(99));
    EXPECT_EQ(bogus.board, Sx126xBoard::Unspecified);

    std::int8_t lo = 0, hi = 0;
    hal::packet_radio_power_range(static_cast<Sx126xBoard>(99), lo, hi);
    EXPECT_EQ(lo, 0);
    EXPECT_EQ(hi, 0);
}

TEST(Sx126xProfiles, BothBoardsShareAWiringButNotAPowerModel) {
    const auto& stick = hal::sx126x_profile(Sx126xBoard::MeshStick);
    const auto& toad  = hal::sx126x_profile(Sx126xBoard::MeshToad);

    // Confirmed against lora-usb-meshstick-1262.yaml and
    // lora-usb-meshtoad-e22.yaml, which are identical apart from comments.
    EXPECT_EQ(stick.has_rxen, toad.has_rxen);
    EXPECT_EQ(stick.dio2_as_rf_switch, toad.dio2_as_rf_switch);
    EXPECT_EQ(stick.dio3_tcxo, toad.dio3_tcxo);
    EXPECT_EQ(stick.tcxo_voltage, toad.tcxo_voltage);

    EXPECT_EQ(stick.pa_gain_db, 0);
    EXPECT_GT(toad.pa_gain_db, 0);
    EXPECT_EQ(stick.max_out_dbm, 22);
    EXPECT_EQ(toad.max_out_dbm, 30);
}

TEST(Sx126xAirtime, MatchesTheSemtechFormulaForLongFast) {
    // SF11 / 250 kHz / 4-5, 16-symbol preamble, explicit header, CRC on:
    // 20.25 preamble symbols + 38 payload symbols at 8.192 ms each.
    const double t = hal::lora_airtime_seconds(params(11, 250'000, 5), 30);
    EXPECT_NEAR(t, 0.477184, 1e-6);
}

TEST(Sx126xAirtime, MatchesTheSemtechFormulaForShortFast) {
    // SF7 / 125 kHz / 4-5: 20.736 ms preamble + 43 symbols at 1.024 ms.
    const double t = hal::lora_airtime_seconds(params(7, 125'000, 5), 20);
    EXPECT_NEAR(t, 0.064768, 1e-6);
}

TEST(Sx126xAirtime, GrowsWithSpreadingFactorAndPayload) {
    const auto p = params(9, 125'000, 5);
    EXPECT_GT(hal::lora_airtime_seconds(p, 200), hal::lora_airtime_seconds(p, 20));
    EXPECT_GT(hal::lora_airtime_seconds(params(12, 125'000, 5), 50),
              hal::lora_airtime_seconds(params(7, 125'000, 5), 50));
}

TEST(Sx126xAirtime, LowDataRateOptimizeLengthensTheFrame) {
    // LDRO drops two bits per symbol, so the same payload needs more symbols.
    EXPECT_GT(hal::lora_airtime_seconds(params(12, 125'000, 8, true), 50),
              hal::lora_airtime_seconds(params(12, 125'000, 8, false), 50));
}
