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

TEST(Sx126xTxBand, PermitsAFrequencyInsideTheDeclaredBand) {
    std::string reason = "unset";
    EXPECT_TRUE(hal::sx126x_tx_frequency_permitted(913'125'000, 902'000'000,
                                                   928'000'000, reason));
    EXPECT_EQ(reason, "unset") << "a permitted frequency must not write a reason";

    // Both edges are inclusive: a region's band start and end are legal places
    // to sit, and rejecting them would make the top and bottom slots unusable.
    EXPECT_TRUE(hal::sx126x_tx_frequency_permitted(902'000'000, 902'000'000,
                                                   928'000'000, reason));
    EXPECT_TRUE(hal::sx126x_tx_frequency_permitted(928'000'000, 902'000'000,
                                                   928'000'000, reason));
}

TEST(Sx126xTxBand, RefusesOutsideTheDeclaredBandAndSaysWhy) {
    std::string reason;
    // The case this exists for: an 868 or 915 stick pointed at 433, where the
    // front end is hundreds of MHz off and the PA sees a bad load.
    EXPECT_FALSE(hal::sx126x_tx_frequency_permitted(433'500'000, 902'000'000,
                                                    928'000'000, reason));
    EXPECT_NE(reason.find("433.5"), std::string::npos) << reason;
    EXPECT_NE(reason.find("902"), std::string::npos) << reason;

    reason.clear();
    EXPECT_FALSE(hal::sx126x_tx_frequency_permitted(901'000'000, 902'000'000,
                                                    928'000'000, reason));
    EXPECT_FALSE(reason.empty());
}

TEST(Sx126xTxBand, EnforcesTheChipRangeEvenWithNoBandDeclared) {
    // Zero limits mean the operator's region was never pushed down. That must
    // not gate ordinary transmits, but the chip's own range still holds — it is
    // a property of the silicon, not of anyone's front end.
    std::string reason = "unset";
    EXPECT_TRUE(hal::sx126x_tx_frequency_permitted(915'000'000, 0, 0, reason));
    EXPECT_EQ(reason, "unset");

    // 2.4 GHz is the reachable-by-accident case: Region.LORA_24 is in the UI's
    // list, belongs to the SX1280, and would otherwise program a PLL word the
    // radio cannot lock and report success having radiated nothing.
    reason.clear();
    EXPECT_FALSE(hal::sx126x_tx_frequency_permitted(2'450'000'000, 0, 0, reason));
    EXPECT_FALSE(reason.empty());

    reason.clear();
    EXPECT_FALSE(hal::sx126x_tx_frequency_permitted(100'000'000, 0, 0, reason));
    EXPECT_FALSE(reason.empty());
}

TEST(Sx126xTxBand, ChipRangeBeatsAnOverwideDeclaredBand) {
    // A caller declaring a band the chip cannot serve must not widen what the
    // radio will do: the chip check runs first and independently.
    std::string reason;
    EXPECT_FALSE(hal::sx126x_tx_frequency_permitted(2'450'000'000, 1'000'000,
                                                    6'000'000'000, reason));
    EXPECT_NE(reason.find("SX1262"), std::string::npos) << reason;
}

TEST(Sx126xTxBand, ChipRangeEdgesAreInclusive) {
    std::string reason;
    EXPECT_TRUE(hal::sx126x_tx_frequency_permitted(hal::kSx126xMinFreqHz, 0, 0, reason));
    EXPECT_TRUE(hal::sx126x_tx_frequency_permitted(hal::kSx126xMaxFreqHz, 0, 0, reason));
    EXPECT_FALSE(hal::sx126x_tx_frequency_permitted(hal::kSx126xMinFreqHz - 1, 0, 0, reason));
    EXPECT_FALSE(hal::sx126x_tx_frequency_permitted(hal::kSx126xMaxFreqHz + 1, 0, 0, reason));
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

// --- Transports and pin maps ---------------------------------------------

TEST(Sx126xTransport, UsbSticksAreOnTheBridgeAndSpiBoardsAreNot) {
    for (auto board : {Sx126xBoard::MeshStick, Sx126xBoard::MeshToad,
                       Sx126xBoard::Unspecified}) {
        EXPECT_EQ(hal::sx126x_profile(board).transport, hal::Sx126xTransport::Ch341Usb);
    }
    for (auto board : {Sx126xBoard::UConsoleAio, Sx126xBoard::CustomSpi}) {
        EXPECT_EQ(hal::sx126x_profile(board).transport, hal::Sx126xTransport::LinuxSpi);
    }
}

TEST(Sx126xTransport, UConsoleAioCarriesItsMeshtasticdPinMap) {
    const auto& p = hal::sx126x_profile(Sx126xBoard::UConsoleAio);
    // Straight from the AIO V2's own meshtasticd config: SPI1, BUSY 24,
    // NRST 25, DIO1 26, and DIO2 running the RF switch so there is no RXEN.
    EXPECT_EQ(p.spi.spidev, "spidev1.0");
    EXPECT_EQ(p.spi.busy, 24);
    EXPECT_EQ(p.spi.reset, 25);
    EXPECT_EQ(p.spi.dio1, 26);
    EXPECT_EQ(p.spi.rxen, -1);
    EXPECT_FALSE(p.has_rxen);
    EXPECT_TRUE(p.dio2_as_rf_switch);
    EXPECT_TRUE(p.dio3_tcxo);
    // Chip select is the SPI controller's own (SPI1-CE0), not a GPIO we drive.
    EXPECT_LT(p.spi.cs, 0);
    EXPECT_TRUE(p.spi.complete());
}

TEST(Sx126xTransport, UConsoleAioIsABareChipSoItRadiatesWhatItProduces) {
    const auto& p = hal::sx126x_profile(Sx126xBoard::UConsoleAio);
    EXPECT_EQ(p.pa_gain_db, 0);
    EXPECT_EQ(p.max_out_dbm, 22);
    EXPECT_EQ(hal::sx126x_chip_power_dbm(p, 22), 22);
    EXPECT_EQ(hal::sx126x_chip_power_dbm(p, 0), 0);
}

TEST(Sx126xTransport, AnUndeclaredCustomBoardHasNoUsablePinMap) {
    // The default declaration names no lines, so open_spidev() refuses it
    // rather than driving whatever GPIO 0 happens to be wired to.
    hal::set_custom_spi_board(hal::Sx126xCustomSpiBoard{});
    EXPECT_FALSE(hal::sx126x_profile(Sx126xBoard::CustomSpi).spi.complete());
}

TEST(Sx126xTransport, ADeclaredCustomBoardBecomesItsProfile) {
    hal::Sx126xCustomSpiBoard decl{};
    decl.pins.spidev = "spidev0.0";
    decl.pins.busy   = 20;
    decl.pins.reset  = 24;
    decl.pins.dio1   = 16;
    decl.pins.rxen   = 12;
    decl.has_rxen    = true;
    // An E22-style front end: the operator states the gain, because nothing on
    // the bus reports it and assuming zero would under-report by 8 dB.
    decl.pa_gain_db  = 8;
    decl.max_out_dbm = 30;
    decl.min_out_dbm = -1;
    hal::set_custom_spi_board(decl);

    const auto& p = hal::sx126x_profile(Sx126xBoard::CustomSpi);
    EXPECT_TRUE(p.spi.complete());
    EXPECT_EQ(p.spi.busy, 20);
    EXPECT_EQ(p.spi.rxen, 12);
    EXPECT_TRUE(p.has_rxen);
    EXPECT_EQ(p.max_out_dbm, 30);
    // And the declared gain is actually applied to the power arithmetic.
    EXPECT_EQ(hal::sx126x_chip_power_dbm(p, 30), 22);

    hal::set_custom_spi_board(hal::Sx126xCustomSpiBoard{}); // leave it as found
}

TEST(Sx126xTransport, AnUnknownBoardStillFallsBackToOneThatCannotTransmit) {
    // Re-checked with the enum extended: the fallback has to stay the
    // Unspecified profile, not the first SPI board that happens to be added.
    const auto& bogus = hal::sx126x_profile(static_cast<Sx126xBoard>(99));
    EXPECT_EQ(bogus.board, Sx126xBoard::Unspecified);
    EXPECT_EQ(bogus.max_out_dbm, 0);
}
