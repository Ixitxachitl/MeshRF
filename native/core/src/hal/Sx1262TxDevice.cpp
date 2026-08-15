// SPDX-License-Identifier: GPL-3.0-or-later
//
// IPacketTxDevice over a CH341+SX126x USB stick (Elecrow MeshStick, NullHop
// MeshToad V3). Ties the USB bridge (Ch341Transport) to the radio driver
// (Sx126xRadio) and presents the packet-level interface Core::transmit() uses.
#include "mrf/hal/PacketTxDevice.h"

#include "Ch341Transport.h"
#include "Sx126x.h"

#include <memory>
#include <mutex>
#include <string>
#include <utility>

namespace mrf::hal {
namespace {

// Process-global diagnostic for the most recent open attempt, polled from
// managed code through Core::device_status(). Guarded because the UI can read
// it while a reconfiguration writes it.
std::mutex  g_status_mu;
std::string g_status = "not attempted";

void set_status(std::string s) {
    std::lock_guard<std::mutex> lk(g_status_mu);
    g_status = std::move(s);
}

class Sx1262TxDevice final : public IPacketTxDevice {
public:
    Sx1262TxDevice(std::unique_ptr<Ch341Transport> bus,
                   const Sx126xBoardProfile& profile)
        : bus_(std::move(bus)), profile_(profile), radio_(*bus_, profile) {}

    bool begin(std::string& error) { return radio_.begin(error); }

    DeviceInfo info() const override {
        DeviceInfo i{};
        i.serial     = bus_->describe();
        i.board_name = profile_.name;
        return i;
    }

    DeviceKind kind() const override { return DeviceKind::Sx1262; }

    bool transmit(const PacketTxConfig& cfg, std::span<const std::uint8_t> payload,
                  std::string& error) override {
        return radio_.transmit(cfg, payload, error);
    }

    std::int8_t min_power_dbm() const override { return profile_.min_out_dbm; }
    std::int8_t max_power_dbm() const override { return profile_.max_out_dbm; }

private:
    // Declaration order matters: radio_ holds a reference to *bus_, so the
    // transport has to be constructed first and destroyed last.
    std::unique_ptr<Ch341Transport> bus_;
    const Sx126xBoardProfile&       profile_;
    Sx126xRadio                     radio_;
};

} // namespace

std::unique_ptr<IPacketTxDevice> open_packet_tx_device(Sx126xBoard board) {
    // Refused before the USB device is even touched. Opening here would leave
    // a transmitter armed under a power model nobody chose, and the boards
    // cannot be distinguished at runtime to choose one safely.
    if (board == Sx126xBoard::Unspecified) {
        set_status("select which SX1262 stick is connected (MeshStick or "
                   "MeshToad) \xE2\x80\x94 they share USB IDs and cannot be "
                   "told apart, and the wrong one misreports transmit power");
        return nullptr;
    }

    std::string transport_status;
    auto bus = open_ch341(transport_status);
    if (!bus) {
        set_status(transport_status);
        return nullptr;
    }

    const auto& profile = sx126x_profile(board);
    auto dev = std::make_unique<Sx1262TxDevice>(std::move(bus), profile);

    std::string error;
    if (!dev->begin(error)) {
        set_status(std::string(profile.name) + " on " + transport_status + ": " + error);
        return nullptr;
    }

    // Spell out the power model, not just the board name. The two sticks are
    // indistinguishable over USB, so the board is the user's word for it —
    // and a wrong answer is silent in the worst direction: a MeshToad driven
    // as a MeshStick radiates ~8 dB more than the UI says. Stating the
    // arithmetic on every open is what makes that visible.
    std::string power = " \xE2\x80\x94 up to " + std::to_string(profile.max_out_dbm) + " dBm";
    if (profile.pa_gain_db != 0)
        power += " (chip " + std::to_string(profile.max_chip_dbm) + " dBm + " +
                 std::to_string(profile.pa_gain_db) + " dB PA)";
    else
        power += " direct from the chip, no PA";

    set_status(std::string(profile.name) + " ready on " + transport_status + power);
    return dev;
}

bool packet_tx_available() { return ch341_backend_available(); }

const char* packet_tx_status() {
    // Snapshot into thread-local storage under the lock: callers read through
    // the returned pointer after this returns, and g_status can be reassigned
    // by a concurrent open.
    thread_local std::string cache;
    std::lock_guard<std::mutex> lk(g_status_mu);
    cache = g_status;
    return cache.c_str();
}

void packet_tx_power_range(Sx126xBoard board, std::int8_t& min_dbm, std::int8_t& max_dbm) {
    const auto& p = sx126x_profile(board);
    min_dbm = p.min_out_dbm;
    max_dbm = p.max_out_dbm;
}

} // namespace mrf::hal
