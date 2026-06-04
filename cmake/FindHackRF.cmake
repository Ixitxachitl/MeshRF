# SPDX-License-Identifier: GPL-3.0-or-later
#
# FindHackRF.cmake — port of SDRangel's FindLibHACKRF.cmake
# (https://github.com/f4exb/sdrangel/blob/master/cmake/Modules/FindLibHACKRF.cmake)
#
# Search order:
#   1. ${HACKRF_DIR} / ${HACKRF_ROOT} (CMake var or env var)
#   2. pkg-config (libhackrf)
#   3. Common Windows install prefixes (Pothos SDR, vcpkg-installed manually)
#   4. System default paths
#
# Defines (when found):
#   HackRF_FOUND, HackRF_INCLUDE_DIR, HackRF_LIBRARY,
#   imported target HackRF::hackrf

if(NOT DEFINED HACKRF_DIR AND DEFINED ENV{HACKRF_DIR})
    set(HACKRF_DIR "$ENV{HACKRF_DIR}")
endif()
if(NOT DEFINED HACKRF_DIR AND DEFINED ENV{HACKRF_ROOT})
    set(HACKRF_DIR "$ENV{HACKRF_ROOT}")
endif()

find_package(PkgConfig QUIET)
if(PKG_CONFIG_FOUND)
    pkg_check_modules(LIBHACKRF_PKG QUIET libhackrf)
endif()

find_path(HackRF_INCLUDE_DIR
    NAMES libhackrf/hackrf.h
    HINTS
        ${HACKRF_DIR}/include
        ${LIBHACKRF_PKG_INCLUDE_DIRS}
    PATHS
        "C:/Program Files/PothosSDR/include"
        "C:/Program Files/HackRF/include"
        /usr/include
        /usr/local/include
)

find_library(HackRF_LIBRARY
    NAMES hackrf
    HINTS
        ${HACKRF_DIR}/lib
        ${HACKRF_DIR}/bin
        ${LIBHACKRF_PKG_LIBRARY_DIRS}
    PATHS
        "C:/Program Files/PothosSDR/lib"
        "C:/Program Files/HackRF/lib"
        /usr/lib
        /usr/local/lib
)

include(FindPackageHandleStandardArgs)
find_package_handle_standard_args(HackRF
    REQUIRED_VARS HackRF_LIBRARY HackRF_INCLUDE_DIR
)

if(HackRF_FOUND AND NOT TARGET HackRF::hackrf)
    add_library(HackRF::hackrf UNKNOWN IMPORTED)
    set_target_properties(HackRF::hackrf PROPERTIES
        IMPORTED_LOCATION             "${HackRF_LIBRARY}"
        INTERFACE_INCLUDE_DIRECTORIES "${HackRF_INCLUDE_DIR}"
    )
endif()

mark_as_advanced(HackRF_INCLUDE_DIR HackRF_LIBRARY)
