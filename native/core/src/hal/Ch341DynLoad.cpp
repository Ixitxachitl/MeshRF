// SPDX-License-Identifier: GPL-3.0-or-later
#include "Ch341DynLoad.h"

#if defined(_WIN32)

#include <array>
#include <filesystem>
#include <mutex>
#include <string>

namespace mrf::hal::ch341_dyn {
namespace fs = std::filesystem;

namespace {

thread_local std::string g_status = "not attempted";

// The CH341PAR installer drops the 64-bit DLL as CH341DLLA64.DLL in System32
// and the 32-bit one as CH341DLL.DLL in SysWOW64, so try the architecture's
// name first and fall back to the other.
constexpr std::array<const wchar_t*, 2> kDllNames{
#if defined(_WIN64)
    L"CH341DLLA64.DLL",
    L"CH341DLL.DLL",
#else
    L"CH341DLL.DLL",
    L"CH341DLLA64.DLL",
#endif
};

template <typename Fn>
bool resolve(HMODULE m, const char* name, Fn& slot) {
    auto p = ::GetProcAddress(m, name);
    if (!p) return false;
    slot = reinterpret_cast<Fn>(p);
    return true;
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

// Cache the resolved table. CH341OpenDevice/CloseDevice are index-based and
// keep per-index state inside the DLL, so repeatedly LoadLibrary'ing it would
// be pointless churn.
std::mutex g_mu;
bool       g_tried = false;
bool       g_ok    = false;
Api        g_api{};
std::string g_cached_status;

} // namespace

bool load(Api& out) {
    std::lock_guard<std::mutex> lk(g_mu);
    if (g_tried) {
        g_status = g_cached_status;
        if (g_ok) out = g_api;
        return g_ok;
    }
    g_tried = true;

    HMODULE m = nullptr;
    std::wstring loaded_from;

    // 1. Next to the bridge DLL, so a private copy can be vendored if needed.
    const fs::path dir = module_directory();
    for (auto* name : kDllNames) {
        if (m) break;
        std::error_code ec;
        const fs::path candidate = dir / name;
        if (!fs::exists(candidate, ec)) continue;
        m = ::LoadLibraryW(candidate.wstring().c_str());
        if (m) loaded_from = candidate.wstring();
    }

    // 2. Default search order, which finds the CH341PAR install in System32.
    for (auto* name : kDllNames) {
        if (m) break;
        m = ::LoadLibraryW(name);
        if (m) loaded_from = name;
    }

    if (!m) {
        g_cached_status =
            "CH341DLL not found \xE2\x80\x94 install the WCH CH341PAR driver package";
        g_status = g_cached_status;
        return false;
    }

    Api a{};
    bool ok = true;
    ok &= resolve(m, "CH341OpenDevice",    a.CH341OpenDevice);
    ok &= resolve(m, "CH341CloseDevice",   a.CH341CloseDevice);
    ok &= resolve(m, "CH341SetStream",     a.CH341SetStream);
    ok &= resolve(m, "CH341StreamSPI4",    a.CH341StreamSPI4);
    ok &= resolve(m, "CH341Set_D5_D0",     a.CH341Set_D5_D0);
    ok &= resolve(m, "CH341GetStatus",     a.CH341GetStatus);
    ok &= resolve(m, "CH341SetTimeout",    a.CH341SetTimeout);
    ok &= resolve(m, "CH341SetExclusive",  a.CH341SetExclusive);
    ok &= resolve(m, "CH341GetDeviceName", a.CH341GetDeviceName);
    ok &= resolve(m, "CH341GetVerIC",      a.CH341GetVerIC);

    if (!ok) {
        ::FreeLibrary(m);
        g_cached_status = "CH341DLL loaded but is missing expected entry points";
        g_status = g_cached_status;
        return false;
    }

    g_api = a;
    g_ok  = true;
    g_cached_status = "loaded " + fs::path(loaded_from).string();
    g_status = g_cached_status;
    out = a;
    return true;
}

const char* last_status() { return g_status.c_str(); }

} // namespace mrf::hal::ch341_dyn

#endif // _WIN32
