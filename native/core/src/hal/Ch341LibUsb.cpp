// SPDX-License-Identifier: GPL-3.0-or-later
//
// Linux/macOS CH341 transport, speaking the bridge's vendor protocol over
// libusb. Command constants and framing follow flashrom's ch341a_spi driver
// and pine64's libch341-spi-userspace, which are what meshtasticd itself uses
// to drive these same sticks.
//
// On Windows the vendor DLL is used instead (Ch341Windows.cpp) because the
// CH341PAR driver owns the device there and libusb cannot claim it.
#include "Ch341Transport.h"

#if !defined(_WIN32)

#if defined(MRF_HAVE_LIBUSB)

#include <libusb.h>

#include <algorithm>
#include <array>
#include <cstring>
#include <string>
#include <vector>

namespace mrf::hal {
namespace {

constexpr std::uint16_t kVid = 0x1A86;
constexpr std::uint16_t kPid = 0x5512;

constexpr unsigned char kEpOut = 0x02;
constexpr unsigned char kEpIn  = 0x82;
constexpr unsigned int  kUsbTimeoutMs = 2000;

// The bridge moves data in 32-byte USB packets: one command byte plus up to
// 31 payload bytes.
constexpr std::size_t kPacketLen  = 32;
constexpr std::size_t kMaxSpiData = kPacketLen - 1;

enum : std::uint8_t {
    kCmdSetOutput  = 0xA1,
    kCmdSpiStream  = 0xA8,
    kCmdI2cStream  = 0xAA,
    kCmdUioStream  = 0xAB,
    kCmdGetStatus  = 0xA0,

    kI2cStmSet     = 0x60,
    kI2cStmEnd     = 0x00,

    kUioStmDir     = 0x40,
    kUioStmOut     = 0x80,
    kUioStmEnd     = 0x20,
};

// D0-D5 direction, 1 = output. Matches the Windows backend: SCK/MOSI belong to
// the SPI engine, BUSY (D4) is an input, CS/RXen/NRST are driven by us.
constexpr std::uint8_t kDirOut = (1u << kCh341PinCs) |
                                 (1u << kCh341PinRxen) |
                                 (1u << kCh341PinReset) |
                                 (1u << 3) |
                                 (1u << 5);

constexpr std::uint8_t kDataIdle = (1u << kCh341PinCs) | (1u << kCh341PinReset);

// The CH341's SPI engine clocks bytes out least-significant bit first, but the
// SX126x expects MSB first. The Windows DLL flips this in hardware via the
// stream-mode flag; over raw USB it has to be done here, exactly as flashrom
// does.
constexpr std::uint8_t reverse_bits(std::uint8_t v) {
    v = static_cast<std::uint8_t>((v & 0xF0u) >> 4 | (v & 0x0Fu) << 4);
    v = static_cast<std::uint8_t>((v & 0xCCu) >> 2 | (v & 0x33u) << 2);
    v = static_cast<std::uint8_t>((v & 0xAAu) >> 1 | (v & 0x55u) << 1);
    return v;
}

static_assert(reverse_bits(0x01) == 0x80, "bit reversal");
static_assert(reverse_bits(0xA8) == 0x15, "bit reversal");

class Ch341LibUsbTransport final : public Ch341Transport {
public:
    Ch341LibUsbTransport(libusb_context* ctx, libusb_device_handle* h, std::string name)
        : ctx_(ctx), handle_(h), name_(std::move(name)), data_(kDataIdle) {}

    ~Ch341LibUsbTransport() override {
        // Park the radio: CS released, held in reset, RF switch off.
        set_pins((1u << kCh341PinCs));
        libusb_release_interface(handle_, 0);
        libusb_close(handle_);
        libusb_exit(ctx_);
    }

    bool configure() {
        const std::array<std::uint8_t, 3> init{
            kCmdI2cStream,
            static_cast<std::uint8_t>(kI2cStmSet | 0x02), // 400 kHz, single I/O SPI
            kI2cStmEnd,
        };
        if (!bulk_out(init.data(), init.size())) return false;
        return set_pins(data_);
    }

    std::string describe() const override { return name_; }

    bool write_pin(std::uint8_t pin, bool high) override {
        if (pin > 5) return false;
        const std::uint8_t bit = static_cast<std::uint8_t>(1u << pin);
        const std::uint8_t next = static_cast<std::uint8_t>(high ? (data_ | bit)
                                                                : (data_ & ~bit));
        if (next == data_) return true;
        if (!set_pins(next)) return false;
        data_ = next;
        return true;
    }

    bool read_pin(std::uint8_t pin, bool& high) override {
        const std::uint8_t cmd = kCmdGetStatus;
        if (!bulk_out(&cmd, 1)) return false;
        std::array<std::uint8_t, 8> resp{};
        int got = 0;
        if (!bulk_in(resp.data(), resp.size(), got) || got < 1) return false;
        // The status reply reports D0-D7 in its first byte.
        high = (resp[0] & (1u << pin)) != 0;
        return true;
    }

    bool transfer(std::span<const std::uint8_t> tx,
                  std::span<std::uint8_t> rx) override {
        if (!rx.empty() && rx.size() != tx.size()) return false;
        std::size_t done = 0;
        while (done < tx.size()) {
            const std::size_t n =
                std::min({kCh341SpiChunk, kMaxSpiData, tx.size() - done});
            packet_.clear();
            packet_.push_back(kCmdSpiStream);
            for (std::size_t i = 0; i < n; ++i)
                packet_.push_back(reverse_bits(tx[done + i]));
            if (!bulk_out(packet_.data(), packet_.size())) return false;

            in_.resize(n);
            int got = 0;
            if (!bulk_in(in_.data(), in_.size(), got) ||
                static_cast<std::size_t>(got) != n)
                return false;
            if (!rx.empty()) {
                for (std::size_t i = 0; i < n; ++i)
                    rx[done + i] = reverse_bits(in_[i]);
            }
            done += n;
        }
        return true;
    }

private:
    bool set_pins(std::uint8_t levels) {
        const std::array<std::uint8_t, 4> buf{
            kCmdUioStream,
            static_cast<std::uint8_t>(kUioStmOut | (levels & 0x3Fu)),
            static_cast<std::uint8_t>(kUioStmDir | (kDirOut & 0x3Fu)),
            kUioStmEnd,
        };
        return bulk_out(buf.data(), buf.size());
    }

    bool bulk_out(const std::uint8_t* data, std::size_t len) {
        int transferred = 0;
        const int rc = libusb_bulk_transfer(
            handle_, kEpOut, const_cast<unsigned char*>(data),
            static_cast<int>(len), &transferred, kUsbTimeoutMs);
        return rc == 0 && static_cast<std::size_t>(transferred) == len;
    }

    bool bulk_in(std::uint8_t* data, std::size_t len, int& transferred) {
        const int rc = libusb_bulk_transfer(handle_, kEpIn, data,
                                            static_cast<int>(len),
                                            &transferred, kUsbTimeoutMs);
        return rc == 0;
    }

    libusb_context*           ctx_;
    libusb_device_handle*     handle_;
    std::string               name_;
    std::uint8_t              data_;
    std::vector<std::uint8_t> packet_;
    std::vector<std::uint8_t> in_;
};

} // namespace

std::unique_ptr<Ch341Transport> open_ch341(std::string& status) {
    libusb_context* ctx = nullptr;
    if (libusb_init(&ctx) != 0) {
        status = "libusb_init failed";
        return nullptr;
    }

    libusb_device_handle* h =
        libusb_open_device_with_vid_pid(ctx, kVid, kPid);
    if (!h) {
        libusb_exit(ctx);
        status = "no CH341 device found (1a86:5512)";
        return nullptr;
    }

    // On Linux the ch341 usb-serial module usually grabs the device first.
    libusb_set_auto_detach_kernel_driver(h, 1);
    if (const int rc = libusb_claim_interface(h, 0); rc != 0) {
        libusb_close(h);
        libusb_exit(ctx);
        status = std::string("could not claim CH341 interface: ") +
                 libusb_error_name(rc) +
                 " (blacklist the ch341 kernel module, or check udev permissions)";
        return nullptr;
    }

    std::string name = "CH341 (1a86:5512)";
    libusb_device_descriptor desc{};
    if (libusb_get_device_descriptor(libusb_get_device(h), &desc) == 0 &&
        desc.iSerialNumber != 0) {
        unsigned char serial[64] = {};
        if (libusb_get_string_descriptor_ascii(h, desc.iSerialNumber, serial,
                                               sizeof(serial)) > 0) {
            name += " serial ";
            name += reinterpret_cast<const char*>(serial);
        }
    }

    auto dev = std::make_unique<Ch341LibUsbTransport>(ctx, h, std::move(name));
    if (!dev->configure()) {
        status = "CH341 claimed but SPI setup failed";
        return nullptr; // destructor releases and closes
    }
    status = dev->describe();
    return dev;
}

bool ch341_backend_available() {
    libusb_context* ctx = nullptr;
    if (libusb_init(&ctx) != 0) return false;
    libusb_exit(ctx);
    return true;
}

} // namespace mrf::hal

#else // !MRF_HAVE_LIBUSB

#include <string>

namespace mrf::hal {

std::unique_ptr<Ch341Transport> open_ch341(std::string& status) {
    status = "built without libusb; SX1262 USB sticks are unavailable";
    return nullptr;
}

bool ch341_backend_available() { return false; }

} // namespace mrf::hal

#endif // MRF_HAVE_LIBUSB

#endif // !_WIN32
