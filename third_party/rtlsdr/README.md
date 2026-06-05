rtlsdr.dll vendored from the rtl-sdr (librtlsdr) project
=======================================================

This directory contains a binary copy of `rtlsdr.dll` (Osmocom librtlsdr),
vendored so that builds of MeshRF do not require a separate SDR install
on Windows. The DLL dynamically links `libusb-1.0.dll`, which is already
vendored in `third_party/hackrf/` and shared by both backends.

Upstream: https://github.com/osmocom/rtl-sdr
Upstream license: GPL-2.0-or-later. See
https://github.com/osmocom/rtl-sdr/blob/master/COPYING

Compatibility: GPL-2.0-or-later is compatible with this project's
GPL-3.0-or-later license; the combined work is licensed under GPL-3.0.

The DLL exposes the standard `rtlsdr_*` C ABI which we resolve at runtime via
`native/core/src/hal/RtlSdrDynLoad.cpp`. We deliberately do NOT depend on the
rtl-sdr SDK headers or import library at build time, and the loader only uses
this bundled copy (plus an explicit RTLSDR_DIR override) — it never probes for
third-party SDR installs such as SDRangel or PothosSDR.

To update:
  1. Drop in a fresh `rtlsdr.dll` from an rtl-sdr release build, SDRangel, or
     PothosSDR (all are the same library).
  2. Re-run `cmake --build build/windows-x64 --config RelWithDebInfo` to copy it.
