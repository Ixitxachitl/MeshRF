// SPDX-License-Identifier: GPL-3.0-or-later
//
// Windows CH341 transport over WCH's CH341DLL. See Ch341DynLoad.h for why the
// vendor DLL is preferred over libusb on this platform.
#include "Ch341Transport.h"

#if defined(_WIN32)

#include "Ch341DynLoad.h"

#include <algorithm>
#include <cstring>
#include <string>
#include <vector>

namespace mrf::hal {
namespace {

// D0-D5 direction, 1 = output. D3 (SCK) and D5 (MOSI) belong to the SPI
// engine but still have to be declared as outputs; D4 (BUSY) stays an input.
// D6 (DIO1) is input-only in hardware and is not covered by Set_D5_D0 at all.
constexpr std::uint32_t kDirOut = (1u << kCh341PinCs) |
                                  (1u << kCh341PinRxen) |
                                  (1u << kCh341PinReset) |
                                  (1u << 3) |  // SCK
                                  (1u << 5);   // MOSI

// Idle levels: CS high (deasserted), NRST high (out of reset), RXen low,
// SCK low (SPI mode 0).
constexpr std::uint32_t kDataIdle = (1u << kCh341PinCs) | (1u << kCh341PinReset);

class Ch341WindowsTransport final : public Ch341Transport {
public:
    Ch341WindowsTransport(const ch341_dyn::Api& api, ULONG index, std::string name)
        : api_(api), index_(index), name_(std::move(name)), data_(kDataIdle) {}

    ~Ch341WindowsTransport() override {
        // Park the radio: CS released, held in reset, RF switch off.
        api_.CH341Set_D5_D0(index_, kDirOut, (1u << kCh341PinCs));
        api_.CH341CloseDevice(index_);
    }

    // Applies the stream mode and drives the pins to their idle state. Split
    // out of the constructor so a half-configured device is never handed back.
    bool configure() {
        if (!api_.CH341SetStream(index_, ch341_dyn::kStreamModeSpiMsbFirst))
            return false;
        // Generous, but still bounded: a stalled bridge must not wedge the
        // caller's transmit thread indefinitely.
        api_.CH341SetTimeout(index_, 2000u, 2000u);
        return api_.CH341Set_D5_D0(index_, kDirOut, data_) != FALSE;
    }

    std::string describe() const override { return name_; }

    bool write_pin(std::uint8_t pin, bool high) override {
        if (pin > 5) return false; // D6/D7 are not writable
        const std::uint32_t bit = 1u << pin;
        const std::uint32_t next = high ? (data_ | bit) : (data_ & ~bit);
        if (next == data_) return true;
        if (!api_.CH341Set_D5_D0(index_, kDirOut, next)) return false;
        data_ = next;
        return true;
    }

    bool read_pin(std::uint8_t pin, bool& high) override {
        ULONG status = 0;
        if (!api_.CH341GetStatus(index_, &status)) return false;
        // CH341GetStatus reports D0-D7 in the low byte.
        high = (status & (1u << pin)) != 0;
        return true;
    }

    bool transfer(std::span<const std::uint8_t> tx,
                  std::span<std::uint8_t> rx) override {
        if (!rx.empty() && rx.size() != tx.size()) return false;
        std::size_t done = 0;
        while (done < tx.size()) {
            const std::size_t n = std::min(kCh341SpiChunk, tx.size() - done);
            scratch_.assign(tx.begin() + static_cast<std::ptrdiff_t>(done),
                            tx.begin() + static_cast<std::ptrdiff_t>(done + n));
            // CH341StreamSPI4 is in-place: the buffer carries MOSI in and is
            // overwritten with MISO. Chip-select is passed as "ignore" because
            // we hold CS ourselves across the whole command.
            if (!api_.CH341StreamSPI4(index_, ch341_dyn::kChipSelectIgnore,
                                      static_cast<ULONG>(n), scratch_.data()))
                return false;
            if (!rx.empty())
                std::memcpy(rx.data() + done, scratch_.data(), n);
            done += n;
        }
        return true;
    }

private:
    ch341_dyn::Api            api_;
    ULONG                     index_;
    std::string               name_;
    std::uint32_t             data_;
    std::vector<std::uint8_t> scratch_;
};

} // namespace

std::unique_ptr<Ch341Transport> open_ch341(std::string& status) {
    ch341_dyn::Api api{};
    if (!ch341_dyn::load(api)) {
        status = ch341_dyn::last_status();
        return nullptr;
    }

    // CH341DLL addresses devices by index rather than by VID/PID, so probe.
    // The first one that opens and configures wins; with more than one stick
    // attached, describe() reports which was taken so the log is unambiguous.
    constexpr ULONG kMaxIndex = 8;
    for (ULONG i = 0; i < kMaxIndex; ++i) {
        const HANDLE h = api.CH341OpenDevice(i);
        if (h == INVALID_HANDLE_VALUE || h == nullptr) continue;

        // Best-effort: keep another CH341 app from stealing the bridge
        // mid-burst. Not fatal if the driver refuses.
        api.CH341SetExclusive(i, 1);

        std::string name = "CH341 #" + std::to_string(i);
        if (const auto* raw = static_cast<const char*>(api.CH341GetDeviceName(i));
            raw && *raw) {
            name += " (";
            name += raw;
            name += ")";
        }

        auto dev = std::make_unique<Ch341WindowsTransport>(api, i, std::move(name));
        if (!dev->configure()) {
            status = "CH341 #" + std::to_string(i) + " opened but SPI setup failed";
            continue; // destructor closes the handle
        }
        status = dev->describe();
        return dev;
    }

    status = "no CH341 device found (probed index 0.." +
             std::to_string(kMaxIndex - 1) + ")";
    return nullptr;
}

bool ch341_backend_available() {
    ch341_dyn::Api api{};
    return ch341_dyn::load(api);
}

} // namespace mrf::hal

#endif // _WIN32
