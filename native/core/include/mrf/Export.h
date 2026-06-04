// SPDX-License-Identifier: GPL-3.0-or-later
#pragma once

// Cross-platform export macro for the C ABI shipped to managed code.
#if defined(_WIN32)
#  if defined(MRF_BUILDING_BRIDGE)
#    define MRF_API __declspec(dllexport)
#  else
#    define MRF_API __declspec(dllimport)
#  endif
#  define MRF_CALL __cdecl
#else
#  define MRF_API __attribute__((visibility("default")))
#  define MRF_CALL
#endif
