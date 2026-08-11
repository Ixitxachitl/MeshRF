// SPDX-License-Identifier: GPL-3.0-or-later
#include "HackRfDynLoad.h"

#if defined(_WIN32)

#include <array>
#include <cstdlib>
#include <filesystem>
#include <string>

namespace mrf::hal::hackrf_dyn {
namespace fs = std::filesystem;

namespace {

thread_local std::string g_status = "not attempted";

std::string narrow(const fs::path& p) {
    return p.string();
}

template <typename Fn>
bool resolve(HMODULE m, const char* name, Fn& slot) {
    auto p = ::GetProcAddress(m, name);
    if (!p) return false;
    slot = reinterpret_cast<Fn>(p);
    return true;
}

HMODULE try_load_explicit(const fs::path& dir, std::string& diag) {
    if (dir.empty()) return nullptr;
    std::error_code ec;
    if (!fs::exists(dir, ec)) return nullptr;
    auto dll = dir / L"hackrf.dll";
    if (!fs::exists(dll, ec)) return nullptr;
    HMODULE m = ::LoadLibraryW(dll.wstring().c_str());
    if (!m) {
        DWORD err = ::GetLastError();
        diag = "LoadLibrary failed for " + dll.string() +
               " (Win32 error " + std::to_string(err) +
               " \xE2\x80\x94 likely a missing dependency: libusb-1.0.dll / pthreadVC2.dll)";
    }
    return m;
}

fs::path module_directory() {
    HMODULE here = nullptr;
    ::GetModuleHandleExW(
        GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS |
            GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
        reinterpret_cast<LPCWSTR>(&module_directory),
        &here);
    wchar_t buf[MAX_PATH] = {};
    ::GetModuleFileNameW(here, buf, MAX_PATH);
    return fs::path(buf).parent_path();
}

fs::path env_path(const wchar_t* name) {
    wchar_t buf[1024] = {};
    DWORD n = ::GetEnvironmentVariableW(name, buf, 1024);
    if (n == 0 || n >= 1024) return {};
    return fs::path(buf);
}

} // namespace

bool load(Api& out) {
    HMODULE m = nullptr;
    fs::path loaded_from;
    std::string last_load_error;

    auto try_dir = [&](const fs::path& dir) {
        if (m) return;
        std::string diag;
        if (auto h = try_load_explicit(dir, diag)) {
            m = h;
            loaded_from = dir / L"hackrf.dll";
        } else if (!diag.empty()) {
            last_load_error = diag;
        }
    };

    // 1. Same directory as the bridge DLL (vendored case).
    try_dir(module_directory());

    // 2. HACKRF_DIR / HACKRF_ROOT environment variables.
    try_dir(env_path(L"HACKRF_DIR"));
    try_dir(env_path(L"HACKRF_ROOT"));
    if (!m) {
        auto root = env_path(L"HACKRF_DIR");
        if (!root.empty()) try_dir(root / L"bin");
    }

    // 3/4. Common installer locations.
    static const std::array<const wchar_t*, 4> kWellKnown{
        L"C:/Program Files/SDRangel",
        L"C:/Program Files/PothosSDR/bin",
        L"C:/Program Files/HackRF/bin",
        L"C:/Program Files/HackRF",
    };
    for (auto* p : kWellKnown) {
        if (m) break;
        try_dir(p);
    }

    // 5. Plain LoadLibrary — relies on PATH / app dir / etc.
    if (!m) {
        m = ::LoadLibraryW(L"hackrf.dll");
        if (m) loaded_from = L"hackrf.dll (default search path)";
    }
    if (!m) {
        g_status = last_load_error.empty()
                       ? "hackrf.dll not found in any search location"
                       : last_load_error;
        return false;
    }

    Api a{};
    bool ok = true;
    ok &= resolve(m, "hackrf_init",            a.hackrf_init);
    ok &= resolve(m, "hackrf_exit",            a.hackrf_exit);
    ok &= resolve(m, "hackrf_open",            a.hackrf_open);
    ok &= resolve(m, "hackrf_close",           a.hackrf_close);
    ok &= resolve(m, "hackrf_set_freq",        a.hackrf_set_freq);
    ok &= resolve(m, "hackrf_set_sample_rate", a.hackrf_set_sample_rate);
    ok &= resolve(m, "hackrf_set_baseband_filter_bandwidth",
                  a.hackrf_set_baseband_filter_bandwidth);
    ok &= resolve(m, "hackrf_compute_baseband_filter_bw_round_down_lt",
                  a.hackrf_compute_baseband_filter_bw_round_down_lt);
    ok &= resolve(m, "hackrf_set_lna_gain",    a.hackrf_set_lna_gain);
    ok &= resolve(m, "hackrf_set_vga_gain",    a.hackrf_set_vga_gain);
    ok &= resolve(m, "hackrf_set_txvga_gain",  a.hackrf_set_txvga_gain);
    ok &= resolve(m, "hackrf_set_amp_enable",  a.hackrf_set_amp_enable);
    ok &= resolve(m, "hackrf_start_rx",        a.hackrf_start_rx);
    ok &= resolve(m, "hackrf_stop_rx",         a.hackrf_stop_rx);
    ok &= resolve(m, "hackrf_start_tx",        a.hackrf_start_tx);
    ok &= resolve(m, "hackrf_stop_tx",         a.hackrf_stop_tx);
    if (!ok) {
        ::FreeLibrary(m);
        g_status = "hackrf.dll loaded but missing required exports";
        return false;
    }
    out = a;
    g_status = "loaded " + narrow(loaded_from);
    // Intentionally leak `m`: we want hackrf.dll to live for the process.
    return true;
}

const char* last_status() { return g_status.c_str(); }

} // namespace mrf::hal::hackrf_dyn

#elif defined(__linux__) || defined(__APPLE__)

#include <dlfcn.h>

#include <array>
#include <cstdlib>
#include <filesystem>
#include <string>

namespace mrf::hal::hackrf_dyn {
namespace fs = std::filesystem;

namespace {

thread_local std::string g_status = "not attempted";

template <typename Fn>
bool resolve(void* m, const char* name, Fn& slot) {
    auto p = ::dlsym(m, name);
    if (!p) return false;
    slot = reinterpret_cast<Fn>(p);
    return true;
}

void* try_load_explicit(const fs::path& dir, std::string& diag) {
    if (dir.empty()) return nullptr;
    std::error_code ec;
    if (!fs::exists(dir, ec)) return nullptr;
#if defined(__APPLE__)
    static const std::array<const char*, 2> kNames{"libhackrf.dylib", "libhackrf.0.dylib"};
#else
    static const std::array<const char*, 2> kNames{"libhackrf.so.0", "libhackrf.so"};
#endif
    for (const char* name : kNames) {
        auto lib = dir / name;
        if (!fs::exists(lib, ec)) continue;
        void* m = ::dlopen(lib.c_str(), RTLD_NOW | RTLD_LOCAL);
        if (m) return m;
        diag = "dlopen failed for " + lib.string() + " (" + ::dlerror() + ")";
    }
    return nullptr;
}

fs::path env_path(const char* name) {
    const char* v = std::getenv(name);
    if (!v || !*v) return {};
    return fs::path(v);
}

} // namespace

bool load(Api& out) {
    void* m = nullptr;
    fs::path loaded_from;
    std::string last_load_error;

    auto try_dir = [&](const fs::path& dir) {
        if (m) return;
        std::string diag;
        if (auto h = try_load_explicit(dir, diag)) {
            m = h;
            loaded_from = dir;
        } else if (!diag.empty()) {
            last_load_error = diag;
        }
    };

    // 1. HACKRF_DIR / HACKRF_ROOT environment variables (explicit opt-in).
    try_dir(env_path("HACKRF_DIR"));
    try_dir(env_path("HACKRF_ROOT"));

    // 2. System library search path — the normal case on Linux, where
    //    libhackrf is installed via the distro package manager (libhackrf0)
    //    or a system-wide `make install` and lives in the ld.so cache.
    if (!m) {
#if defined(__APPLE__)
        static const std::array<const char*, 2> kNames{"libhackrf.dylib", "libhackrf.0.dylib"};
#else
        static const std::array<const char*, 2> kNames{"libhackrf.so.0", "libhackrf.so"};
#endif
        for (const char* name : kNames) {
            m = ::dlopen(name, RTLD_NOW | RTLD_LOCAL);
            if (m) {
                loaded_from = name;
                break;
            }
        }
    }
    if (!m) {
        g_status = last_load_error.empty()
                       ? "libhackrf not found (install libhackrf0, or set HACKRF_DIR)"
                       : last_load_error;
        return false;
    }

    Api a{};
    bool ok = true;
    ok &= resolve(m, "hackrf_init",            a.hackrf_init);
    ok &= resolve(m, "hackrf_exit",            a.hackrf_exit);
    ok &= resolve(m, "hackrf_open",            a.hackrf_open);
    ok &= resolve(m, "hackrf_close",           a.hackrf_close);
    ok &= resolve(m, "hackrf_set_freq",        a.hackrf_set_freq);
    ok &= resolve(m, "hackrf_set_sample_rate", a.hackrf_set_sample_rate);
    ok &= resolve(m, "hackrf_set_baseband_filter_bandwidth",
                  a.hackrf_set_baseband_filter_bandwidth);
    ok &= resolve(m, "hackrf_compute_baseband_filter_bw_round_down_lt",
                  a.hackrf_compute_baseband_filter_bw_round_down_lt);
    ok &= resolve(m, "hackrf_set_lna_gain",    a.hackrf_set_lna_gain);
    ok &= resolve(m, "hackrf_set_vga_gain",    a.hackrf_set_vga_gain);
    ok &= resolve(m, "hackrf_set_txvga_gain",  a.hackrf_set_txvga_gain);
    ok &= resolve(m, "hackrf_set_amp_enable",  a.hackrf_set_amp_enable);
    ok &= resolve(m, "hackrf_start_rx",        a.hackrf_start_rx);
    ok &= resolve(m, "hackrf_stop_rx",         a.hackrf_stop_rx);
    ok &= resolve(m, "hackrf_start_tx",        a.hackrf_start_tx);
    ok &= resolve(m, "hackrf_stop_tx",         a.hackrf_stop_tx);
    if (!ok) {
        ::dlclose(m);
        g_status = "libhackrf loaded but missing required exports";
        return false;
    }
    out = a;
    g_status = "loaded " + loaded_from.string();
    // Intentionally leak `m`: we want libhackrf to live for the process.
    return true;
}

const char* last_status() { return g_status.c_str(); }

} // namespace mrf::hal::hackrf_dyn

#else  // unsupported platform

namespace mrf::hal::hackrf_dyn {
bool load(Api&) { return false; }
const char* last_status() { return "unsupported platform"; }
}

#endif
