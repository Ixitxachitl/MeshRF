// SPDX-License-Identifier: GPL-3.0-or-later
#include "RtlSdrDynLoad.h"

#if defined(_WIN32)

#define WIN32_LEAN_AND_MEAN
#include <windows.h>

#include <filesystem>
#include <string>

namespace mrf::hal::rtlsdr_dyn {
namespace fs = std::filesystem;

namespace {

thread_local std::string g_status = "not attempted";

std::string narrow(const fs::path& p) { return p.string(); }

template <typename Fn>
bool resolve(HMODULE m, const char* name, Fn& slot) {
    auto p = ::GetProcAddress(m, name);
    if (!p) return false;
    slot = reinterpret_cast<Fn>(p);
    return true;
}

template <typename Fn>
void resolve_optional(HMODULE m, const char* name, Fn& slot) {
    auto p = ::GetProcAddress(m, name);
    slot = p ? reinterpret_cast<Fn>(p) : nullptr;
}

HMODULE try_load_explicit(const fs::path& dir, std::string& diag) {
    if (dir.empty()) return nullptr;
    std::error_code ec;
    if (!fs::exists(dir, ec)) return nullptr;
    // PothosSDR/zadig ship "rtlsdr.dll"; some builds use "librtlsdr.dll".
    for (const wchar_t* name : {L"rtlsdr.dll", L"librtlsdr.dll"}) {
        auto dll = dir / name;
        if (!fs::exists(dll, ec)) continue;
        HMODULE m = ::LoadLibraryW(dll.wstring().c_str());
        if (m) return m;
        DWORD err = ::GetLastError();
        diag = "LoadLibrary failed for " + dll.string() +
               " (Win32 error " + std::to_string(err) +
               " \xE2\x80\x94 likely a missing dependency: libusb-1.0.dll)";
    }
    return nullptr;
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
            loaded_from = dir;
        } else if (!diag.empty()) {
            last_load_error = diag;
        }
    };

    // 1. Same directory as the bridge DLL (the bundled, vendored copy). This is
    //    the only location used by default — we deliberately do NOT probe for
    //    third-party installs (SDRangel, PothosSDR, etc.) so the app always runs
    //    against the rtlsdr.dll we ship and never silently binds to whatever
    //    happens to be installed on the machine.
    try_dir(module_directory());

    // 2. Explicit opt-in override via RTLSDR_DIR / RTLSDR_ROOT (and their bin/).
    //    This is an intentional user choice, not auto-detection.
    if (!m) try_dir(env_path(L"RTLSDR_DIR"));
    if (!m) try_dir(env_path(L"RTLSDR_ROOT"));
    if (!m) {
        auto root = env_path(L"RTLSDR_DIR");
        if (!root.empty()) try_dir(root / L"bin");
    }

    if (!m) {
        g_status = last_load_error.empty()
                       ? "bundled rtlsdr.dll not found next to the app "
                         "(set RTLSDR_DIR to override)"
                       : last_load_error;
        return false;
    }

    Api a{};
    bool ok = true;
    ok &= resolve(m, "rtlsdr_get_device_count",   a.rtlsdr_get_device_count);
    ok &= resolve(m, "rtlsdr_open",               a.rtlsdr_open);
    ok &= resolve(m, "rtlsdr_close",              a.rtlsdr_close);
    ok &= resolve(m, "rtlsdr_set_center_freq",    a.rtlsdr_set_center_freq);
    ok &= resolve(m, "rtlsdr_set_sample_rate",    a.rtlsdr_set_sample_rate);
    ok &= resolve(m, "rtlsdr_set_tuner_gain_mode", a.rtlsdr_set_tuner_gain_mode);
    ok &= resolve(m, "rtlsdr_set_tuner_gain",     a.rtlsdr_set_tuner_gain);
    ok &= resolve(m, "rtlsdr_reset_buffer",       a.rtlsdr_reset_buffer);
    ok &= resolve(m, "rtlsdr_read_async",         a.rtlsdr_read_async);
    ok &= resolve(m, "rtlsdr_cancel_async",       a.rtlsdr_cancel_async);
    resolve_optional(m, "rtlsdr_set_agc_mode",       a.rtlsdr_set_agc_mode);
    resolve_optional(m, "rtlsdr_set_freq_correction", a.rtlsdr_set_freq_correction);
    resolve_optional(m, "rtlsdr_set_bias_tee",       a.rtlsdr_set_bias_tee);
    resolve_optional(m, "rtlsdr_get_tuner_gains",    a.rtlsdr_get_tuner_gains);
    resolve_optional(m, "rtlsdr_get_device_name",    a.rtlsdr_get_device_name);
    if (!ok) {
        ::FreeLibrary(m);
        g_status = "rtlsdr.dll loaded but missing required exports";
        return false;
    }
    out = a;
    g_status = "loaded " + narrow(loaded_from);
    // Intentionally leak `m`: we want rtlsdr.dll to live for the process.
    return true;
}

const char* last_status() { return g_status.c_str(); }

} // namespace mrf::hal::rtlsdr_dyn

#else // !_WIN32

namespace mrf::hal::rtlsdr_dyn {
bool load(Api&) { return false; }
const char* last_status() { return "unsupported platform"; }
}

#endif
