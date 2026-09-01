// SPDX-License-Identifier: GPL-3.0-or-later
//
// Linux spidev + GPIO character device implementation of Sx126xBus.
// See SpiDevBus.h for the wiring model and why libgpiod is not used.
#include "SpiDevBus.h"

#include <string>

#if defined(__linux__)

#include <linux/gpio.h>
#include <linux/spi/spidev.h>

#include <dirent.h>
#include <fcntl.h>
#include <sys/ioctl.h>
#include <unistd.h>

#include <cerrno>
#include <cstdint>
#include <cstdio>
#include <cstring>

namespace mrf::hal {
namespace {

std::string errno_text() { return std::string(std::strerror(errno)); }

// Owning file descriptor. Every fd here outlives individual calls and has to
// be closed on any failure path, of which there are many.
class Fd {
public:
    Fd() = default;
    explicit Fd(int fd) : fd_(fd) {}
    ~Fd() { reset(); }
    Fd(const Fd&) = delete;
    Fd& operator=(const Fd&) = delete;
    Fd(Fd&& o) noexcept : fd_(o.fd_) { o.fd_ = -1; }
    Fd& operator=(Fd&& o) noexcept {
        if (this != &o) { reset(); fd_ = o.fd_; o.fd_ = -1; }
        return *this;
    }
    void reset(int fd = -1) {
        if (fd_ >= 0) ::close(fd_);
        fd_ = fd;
    }
    int get() const { return fd_; }
    bool valid() const { return fd_ >= 0; }

private:
    int fd_ = -1;
};

// Claim one GPIO line. Outputs are given their idle level at request time
// rather than after: a line requested as an output defaults low, which on CS
// would be a spurious assert and on NRST a reset pulse.
bool request_line(int chip_fd, int offset, bool output, bool initial_high,
                  Fd& out, std::string& error) {
    struct gpio_v2_line_request req {};
    req.offsets[0]  = static_cast<__u32>(offset);
    req.num_lines   = 1;
    std::snprintf(req.consumer, sizeof(req.consumer), "MeshRF");
    req.config.flags = output ? GPIO_V2_LINE_FLAG_OUTPUT : GPIO_V2_LINE_FLAG_INPUT;

    if (output) {
        req.config.num_attrs             = 1;
        req.config.attrs[0].mask         = 1ULL;
        req.config.attrs[0].attr.id      = GPIO_V2_LINE_ATTR_ID_OUTPUT_VALUES;
        req.config.attrs[0].attr.values  = initial_high ? 1ULL : 0ULL;
    }

    if (::ioctl(chip_fd, GPIO_V2_GET_LINE_IOCTL, &req) < 0) {
        error = "GPIO line " + std::to_string(offset) + ": " + errno_text();
        // EBUSY is worth naming: it means something else already owns the
        // line, and on these boards that something is almost always
        // meshtasticd holding the same radio.
        if (errno == EBUSY)
            error += " (another process holds this line — is meshtasticd running?)";
        return false;
    }
    out.reset(req.fd);
    return true;
}

class SpiDevBus final : public Sx126xBus {
public:
    SpiDevBus(Sx126xSpiPins pins, Fd spi, Fd cs, Fd busy, Fd reset, Fd rxen, Fd dio1)
        : pins_(std::move(pins)), spi_(std::move(spi)), cs_(std::move(cs)),
          busy_(std::move(busy)), reset_(std::move(reset)), rxen_(std::move(rxen)),
          dio1_(std::move(dio1)) {}

    std::string describe() const override {
        return pins_.spidev + " + " + pins_.gpiochip;
    }

    // No serial exists for a soldered-down radio, and none is needed: unlike
    // two identical USB sticks, one spidev node is already unambiguous.
    std::string serial() const override { return pins_.spidev; }

    bool write_pin(std::uint8_t pin, bool high) override {
        // With hardware chip select there is nothing to drive — the controller
        // pulses CS around each transfer itself. See SpiDevBus.h.
        if (pin == kSx126xPinCs && !cs_.valid()) return true;
        // A board with DIO2 running its RF switch has no RXEN line. Report
        // success rather than failure: the driver only writes it when the
        // profile claims one, and refusing here would fail an operation that
        // is genuinely complete.
        if (pin == kSx126xPinRxen && !rxen_.valid()) return true;

        const Fd* line = line_for(pin);
        if (line == nullptr || !line->valid()) return false;

        struct gpio_v2_line_values vals {};
        vals.mask = 1ULL;
        vals.bits = high ? 1ULL : 0ULL;
        return ::ioctl(line->get(), GPIO_V2_LINE_SET_VALUES_IOCTL, &vals) >= 0;
    }

    bool read_pin(std::uint8_t pin, bool& high) override {
        const Fd* line = line_for(pin);
        if (line == nullptr || !line->valid()) return false;

        struct gpio_v2_line_values vals {};
        vals.mask = 1ULL;
        if (::ioctl(line->get(), GPIO_V2_LINE_GET_VALUES_IOCTL, &vals) < 0) return false;
        high = (vals.bits & 1ULL) != 0;
        return true;
    }

    bool transfer(std::span<const std::uint8_t> tx,
                  std::span<std::uint8_t> rx) override {
        if (tx.empty()) return true;
        if (!rx.empty() && rx.size() != tx.size()) return false;

        // Through uintptr_t, not straight to __u64: spi_ioc_transfer always
        // carries 64-bit addresses, but a 32-bit userland (armhf Raspberry Pi
        // OS, which the uConsole can still be running) has 32-bit pointers,
        // and casting one directly to a 64-bit integer is ill-formed.
        struct spi_ioc_transfer tr {};
        tr.tx_buf = static_cast<std::uint64_t>(reinterpret_cast<std::uintptr_t>(tx.data()));
        tr.rx_buf = rx.empty()
                        ? 0
                        : static_cast<std::uint64_t>(reinterpret_cast<std::uintptr_t>(rx.data()));
        tr.len           = static_cast<std::uint32_t>(tx.size());
        tr.speed_hz      = pins_.speed_hz;
        tr.bits_per_word = 8;
        // No chunking, unlike the CH341: the longest SX126x command is a
        // 255-byte buffer write plus its opcode, well inside spidev's 4 KiB
        // default transfer limit.
        return ::ioctl(spi_.get(), SPI_IOC_MESSAGE(1), &tr) >= 0;
    }

private:
    const Fd* line_for(std::uint8_t pin) const {
        switch (pin) {
        case kSx126xPinCs:    return &cs_;
        case kSx126xPinBusy:  return &busy_;
        case kSx126xPinReset: return &reset_;
        case kSx126xPinRxen:  return &rxen_;
        case kSx126xPinDio1:  return &dio1_;
        default:              return nullptr;
        }
    }

    Sx126xSpiPins pins_;
    Fd            spi_;
    Fd            cs_;
    Fd            busy_;
    Fd            reset_;
    Fd            rxen_;
    Fd            dio1_;
};

} // namespace

std::unique_ptr<Sx126xBus> open_spidev(const Sx126xSpiPins& pins, std::string& status) {
    if (!pins.complete()) {
        status = "no pin map: this board needs BUSY, NRST and DIO1 line numbers "
                 "before the radio can be opened";
        return nullptr;
    }

    const std::string spi_path = "/dev/" + pins.spidev;
    Fd spi(::open(spi_path.c_str(), O_RDWR | O_CLOEXEC));
    if (!spi.valid()) {
        status = spi_path + ": " + errno_text();
        if (errno == ENOENT)
            status += " (is the SPI interface enabled? raspi-config, or dtparam=spi=on)";
        return nullptr;
    }

    // SPI mode 0 is what the SX126x clocks on. SPI_NO_CS only when the board
    // wires chip select to a GPIO we drive ourselves.
    std::uint8_t  mode  = SPI_MODE_0;
    if (pins.cs >= 0) mode |= SPI_NO_CS;
    std::uint8_t  bits  = 8;
    std::uint32_t speed = pins.speed_hz;
    if (::ioctl(spi.get(), SPI_IOC_WR_MODE, &mode) < 0 ||
        ::ioctl(spi.get(), SPI_IOC_WR_BITS_PER_WORD, &bits) < 0 ||
        ::ioctl(spi.get(), SPI_IOC_WR_MAX_SPEED_HZ, &speed) < 0) {
        status = spi_path + ": could not configure SPI (" + errno_text() + ")";
        return nullptr;
    }

    const std::string chip_path = "/dev/" + pins.gpiochip;
    Fd chip(::open(chip_path.c_str(), O_RDWR | O_CLOEXEC));
    if (!chip.valid()) {
        status = chip_path + ": " + errno_text();
        return nullptr;
    }

    std::string error;
    Fd cs, busy, reset, rxen, dio1;
    // Idle levels: CS and NRST are both active low, so both idle high.
    if (pins.cs >= 0 && !request_line(chip.get(), pins.cs, true, true, cs, error)) {
        status = chip_path + " CS: " + error;
        return nullptr;
    }
    if (!request_line(chip.get(), pins.reset, true, true, reset, error)) {
        status = chip_path + " NRST: " + error;
        return nullptr;
    }
    if (pins.rxen >= 0 && !request_line(chip.get(), pins.rxen, true, false, rxen, error)) {
        status = chip_path + " RXEN: " + error;
        return nullptr;
    }
    if (!request_line(chip.get(), pins.busy, false, false, busy, error)) {
        status = chip_path + " BUSY: " + error;
        return nullptr;
    }
    if (!request_line(chip.get(), pins.dio1, false, false, dio1, error)) {
        status = chip_path + " DIO1: " + error;
        return nullptr;
    }

    auto bus = std::make_unique<SpiDevBus>(pins, std::move(spi), std::move(cs),
                                           std::move(busy), std::move(reset),
                                           std::move(rxen), std::move(dio1));
    status = bus->describe();
    return bus;
}

bool spidev_backend_available() {
    // Any spidev node at all. The overlay being off is by far the most common
    // reason a correctly wired board has no radio, and it leaves /dev with no
    // spidev entries whatsoever.
    DIR* dir = ::opendir("/dev");
    if (dir == nullptr) return false;
    bool found = false;
    while (const struct dirent* e = ::readdir(dir)) {
        if (std::strncmp(e->d_name, "spidev", 6) == 0) { found = true; break; }
    }
    ::closedir(dir);
    return found;
}

} // namespace mrf::hal

#else // !__linux__

namespace mrf::hal {

std::unique_ptr<Sx126xBus> open_spidev(const Sx126xSpiPins&, std::string& status) {
    status = "spidev is Linux-only; on this platform an SX1262 attaches over a "
             "CH341 USB bridge instead";
    return nullptr;
}

bool spidev_backend_available() { return false; }

} // namespace mrf::hal

#endif // __linux__
