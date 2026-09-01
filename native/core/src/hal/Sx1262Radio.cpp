// SPDX-License-Identifier: GPL-3.0-or-later
//
// IPacketRadio over an SX126x, on either of the buses one can hang off: a
// CH341 USB stick (Elecrow MeshStick, NullHop MeshToad V3) or the host's own
// SPI controller (uConsole AIO V2, Raspberry Pi HATs). Ties the bus to the
// radio driver (Sx126xRadio) and presents the packet-level interface Core uses
// for both directions.
//
// The radio is half-duplex, so receive runs on a private polling thread and a
// transmit borrows it for the length of the burst. Polling rather than the
// DIO1 interrupt line is deliberate: the CH341's interrupt endpoint tops out
// around 400 Hz, and reading the IRQ register costs one SPI round trip we are
// paying for anyway. On spidev the same poll is far cheaper still.
#include "mrf/hal/PacketRadio.h"

#include "Ch341Bus.h"
#include "SpiDevBus.h"
#include "Sx126xBus.h"
#include "Sx126x.h"

#include <atomic>
#include <chrono>
#include <memory>
#include <mutex>
#include <string>
#include <thread>
#include <utility>

namespace mrf::hal {
namespace {

// Process-global diagnostic for the most recent open attempt, polled from
// managed code. Guarded because the UI can read it while a reconfiguration
// writes it.
std::mutex  g_status_mu;
std::string g_status = "not attempted";

void set_status(std::string s) {
    std::lock_guard<std::mutex> lk(g_status_mu);
    g_status = std::move(s);
}

// How often the receive thread asks the radio whether a frame has landed.
// Fast enough that back-to-back frames are never missed (the shortest
// Meshtastic preset is ~15 ms of airtime), slow enough not to saturate a
// 1.4 MHz USB-SPI bridge that a transmit may be waiting to use.
constexpr auto kRxPollInterval = std::chrono::milliseconds(3);

class Sx1262Radio final : public IPacketRadio {
public:
    Sx1262Radio(std::unique_ptr<Sx126xBus> bus, Sx126xBoardProfile profile)
        : bus_(std::move(bus)), profile_(std::move(profile)),
          radio_(*bus_, profile_) {}

    ~Sx1262Radio() override { stop_rx(); }

    bool begin(std::string& error) { return radio_.begin(error); }

    DeviceInfo info() const override {
        DeviceInfo i{};
        i.serial     = bus_->serial();
        i.board_name = profile_.name;
        return i;
    }

    DeviceKind kind() const override { return DeviceKind::Sx1262; }

    bool start_rx(const PacketRadioConfig& cfg, PacketRxCallback cb,
                  std::string& error) override {
        stop_rx();
        {
            std::lock_guard<std::mutex> lk(radio_mu_);
            if (!radio_.enter_rx(cfg, error)) return false;
        }
        rx_cfg_ = cfg;
        rx_cb_  = std::move(cb);
        rx_stop_.store(false, std::memory_order_release);
        rx_thread_ = std::thread([this] { rx_loop(); });
        return true;
    }

    void stop_rx() override {
        rx_stop_.store(true, std::memory_order_release);
        if (rx_thread_.joinable()) rx_thread_.join();
        std::lock_guard<std::mutex> lk(radio_mu_);
        std::string ignored;
        radio_.idle(ignored);
    }

    bool is_rx_running() const override { return rx_thread_.joinable(); }

    bool transmit(const PacketRadioConfig& cfg, std::span<const std::uint8_t> payload,
                  std::string& error) override {
        // Half-duplex: the receive thread is holding this same mutex between
        // polls, so taking it here is what borrows the radio for the burst.
        std::lock_guard<std::mutex> lk(radio_mu_);
        const bool ok = radio_.transmit(cfg, payload, error);
        // Back to listening, whether or not the burst succeeded — leaving the
        // radio in standby after a failed transmit would silently stop a
        // single-stick node receiving.
        if (is_rx_running()) {
            std::string resume_error;
            if (!radio_.enter_rx(rx_cfg_, resume_error) && ok) {
                error = "transmit succeeded but could not resume receive: " + resume_error;
                return false;
            }
        }
        return ok;
    }

    std::int8_t min_power_dbm() const override { return profile_.min_out_dbm; }
    std::int8_t max_power_dbm() const override { return profile_.max_out_dbm; }

private:
    void rx_loop() {
        while (!rx_stop_.load(std::memory_order_acquire)) {
            ReceivedPacket packet;
            bool got = false;
            std::string error;
            {
                std::lock_guard<std::mutex> lk(radio_mu_);
                if (!radio_.poll_rx(packet, got, error)) {
                    // A bridge that has gone away would otherwise spin here
                    // logging forever. Stop instead; Core reports the device
                    // as no longer receiving.
                    break;
                }
            }
            if (got && rx_cb_) rx_cb_(packet);
            // Only sleep when idle: a burst of frames drains at SPI speed.
            if (!got) std::this_thread::sleep_for(kRxPollInterval);
        }
    }

    // Declaration order matters: radio_ holds references to both *bus_ and
    // profile_, so both have to be constructed first and destroyed last. The
    // profile is owned rather than referenced because the Custom SPI board's
    // is assembled per open from the operator's declaration, and so outlives
    // no table.
    std::unique_ptr<Sx126xBus> bus_;
    Sx126xBoardProfile         profile_;
    Sx126xRadio                radio_;

    // Serializes every SPI conversation. Held by the receive thread around
    // each poll and by transmit() for a whole burst.
    std::mutex        radio_mu_;
    std::thread       rx_thread_;
    std::atomic<bool> rx_stop_{true};
    PacketRxCallback  rx_cb_;
    PacketRadioConfig rx_cfg_{};
};

} // namespace

std::unique_ptr<IPacketRadio> open_packet_radio(Sx126xBoard board,
                                                const std::string& serial) {
    // Refused before any device is touched. Opening here would leave a radio
    // armed under a power model nobody chose, and the sticks cannot be
    // distinguished at runtime to choose one safely.
    if (board == Sx126xBoard::Unspecified) {
        set_status("select which SX1262 board this is â the USB sticks "
                   "share USB IDs and cannot be told apart, and the wrong one "
                   "misreports transmit power");
        return nullptr;
    }

    const Sx126xBoardProfile profile = sx126x_profile(board);

    std::string transport_status;
    std::unique_ptr<Sx126xBus> bus;
    if (profile.transport == Sx126xTransport::LinuxSpi) {
        bus = open_spidev(profile.spi, transport_status);
    } else {
        bus = open_ch341(serial, transport_status);
    }
    if (!bus) {
        set_status(transport_status);
        return nullptr;
    }

    auto dev = std::make_unique<Sx1262Radio>(std::move(bus), profile);

    std::string error;
    if (!dev->begin(error)) {
        set_status(std::string(profile.name) + " on " + transport_status + ": " + error);
        return nullptr;
    }

    // Spell out the power model, not just the board name. A board is the
    // user's word for what is attached — over USB because the two sticks are
    // indistinguishable, over SPI because nothing on the bus reports a front
    // end at all — and a wrong answer is silent in the worst direction: a
    // MeshToad driven as a MeshStick radiates ~8 dB more than the UI says.
    // Stating the arithmetic on every open is what makes that visible.
    std::string power = " â up to " + std::to_string(profile.max_out_dbm) + " dBm";
    if (profile.pa_gain_db != 0)
        power += " (chip " + std::to_string(profile.max_chip_dbm) + " dBm + " +
                 std::to_string(profile.pa_gain_db) + " dB PA)";
    else
        power += " direct from the chip, no PA";

    set_status(std::string(profile.name) + " ready on " + transport_status + power);
    return dev;
}

// Either bus counts: the UI offers the SX1262 device when a machine could have
// one, and only the board picker narrows that to a specific transport.
bool packet_radio_available() {
    return ch341_backend_available() || spidev_backend_available();
}

std::vector<std::string> list_packet_radio_serials() {
    // Only the USB sticks have serials, and only they can be ambiguous. An SPI
    // radio is identified by its spidev node, which the board profile already
    // names, so it contributes nothing to a picker whose whole purpose is
    // telling identical sticks apart.
    return list_ch341_serials();
}

const char* packet_radio_status() {
    // Snapshot into thread-local storage under the lock: callers read through
    // the returned pointer after this returns, and g_status can be reassigned
    // by a concurrent open.
    thread_local std::string cache;
    std::lock_guard<std::mutex> lk(g_status_mu);
    cache = g_status;
    return cache.c_str();
}

void packet_radio_power_range(Sx126xBoard board, std::int8_t& min_dbm, std::int8_t& max_dbm) {
    const auto& p = sx126x_profile(board);
    min_dbm = p.min_out_dbm;
    max_dbm = p.max_out_dbm;
}

} // namespace mrf::hal
