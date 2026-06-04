add_library(mrf_core STATIC
    src/Core.cpp
    src/hal/HackRfDevice.cpp
    src/hal/HackRfDynLoad.cpp
    src/hal/RtlSdrDevice.cpp
    src/hal/RtlSdrDynLoad.cpp
    src/dsp/Fir.cpp
    src/dsp/Fft.cpp
    src/dsp/SignalStats.cpp
    src/dsp/Spectrum.cpp
    src/dsp/Resampler.cpp
    src/modem/LoraModem.cpp
    src/modem/ChirpChatRx.cpp
    src/modem/MeshtasticRx.cpp
    src/modem/LoraDecoder.cpp
    src/mac/PacketHeader.cpp
    src/mac/Csma.cpp
    src/crypto/ChannelCrypto.cpp
    src/proto/MeshPacketCodec.cpp
    src/router/FloodingRouter.cpp
)

target_include_directories(mrf_core
    PUBLIC
        $<BUILD_INTERFACE:${CMAKE_CURRENT_SOURCE_DIR}/include>
)

target_compile_features(mrf_core PUBLIC cxx_std_20)

# ---------------------------------------------------------------------------
# Optional dependencies (resolved via vcpkg manifest at top level)
# These are wired in best-effort; modules guarded by HAVE_* macros so the
# scaffold compiles even before vcpkg integration is fully bootstrapped.
# ---------------------------------------------------------------------------
find_package(unofficial-sodium CONFIG QUIET)
if(unofficial-sodium_FOUND)
    target_link_libraries(mrf_core PUBLIC unofficial-sodium::sodium)
    target_compile_definitions(mrf_core PUBLIC MRF_HAVE_SODIUM=1)
endif()

find_package(spdlog CONFIG QUIET)
if(spdlog_FOUND)
    target_link_libraries(mrf_core PUBLIC spdlog::spdlog)
    target_compile_definitions(mrf_core PUBLIC MRF_HAVE_SPDLOG=1)
endif()

# libhackrf is loaded *at runtime* via LoadLibrary (see src/hal/HackRfDynLoad.cpp)
# so there is no build-time dependency on hackrf.h or hackrf.lib. We just need
# to locate hackrf.dll and copy it next to the bridge output. Search order
# matches HackRfDynLoad's runtime search.
set(_hackrf_dll_candidates
    "${CMAKE_SOURCE_DIR}/third_party/hackrf/hackrf.dll"
    "$ENV{HACKRF_DIR}/bin/hackrf.dll"
    "$ENV{HACKRF_DIR}/hackrf.dll"
    "$ENV{HACKRF_ROOT}/bin/hackrf.dll"
    "$ENV{HACKRF_ROOT}/hackrf.dll"
    "C:/Program Files/SDRangel/hackrf.dll"
    "C:/Program Files/PothosSDR/bin/hackrf.dll"
    "C:/Program Files/HackRF/bin/hackrf.dll"
)
unset(MRF_HACKRF_DLL CACHE)
foreach(_p IN LISTS _hackrf_dll_candidates)
    if(_p AND EXISTS "${_p}")
        set(MRF_HACKRF_DLL "${_p}" CACHE INTERNAL "Path to hackrf.dll to vendor")
        break()
    endif()
endforeach()
if(MRF_HACKRF_DLL)
    message(STATUS "mrf_core: vendoring hackrf.dll from ${MRF_HACKRF_DLL}")
else()
    message(STATUS "mrf_core: hackrf.dll not found on this machine; the app will use the synthetic NullDevice. Install SDRangel/PothosSDR or set HACKRF_DIR.")
endif()

set_target_properties(mrf_core PROPERTIES
    POSITION_INDEPENDENT_CODE ON
    FOLDER "native"
)
