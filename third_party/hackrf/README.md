hackrf.dll vendored from the HackRF project
============================================

This directory contains a binary copy of `hackrf.dll` from the upstream
HackRF project at https://github.com/greatscottgadgets/hackrf, vendored so
that builds of MeshtasticRF do not require a separate SDR install on Windows.

Upstream license: GPL-2.0-or-later. See
https://github.com/greatscottgadgets/hackrf/blob/master/COPYING

Compatibility: GPL-2.0-or-later is compatible with this project's
GPL-3.0-or-later license; the combined work is licensed under GPL-3.0.

The DLL exposes the standard `hackrf_*` C ABI which we resolve at runtime via
`native/core/src/hal/HackRfDynLoad.cpp`. We deliberately do NOT depend on the
HackRF SDK headers or import library at build time.

To update:
  1. Drop in a fresh `hackrf.dll` from a HackRF release build, SDRangel, or
     PothosSDR (all are the same library).
  2. Re-run `cmake --build build/windows-x64 --config Debug` to copy it.
