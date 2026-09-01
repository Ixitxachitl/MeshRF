add_library(mrf_core STATIC
    src/Core.cpp
    src/hal/HackRfDevice.cpp
    src/hal/HackRfDynLoad.cpp
    src/hal/RtlSdrDevice.cpp
    src/hal/RtlSdrDynLoad.cpp
    src/hal/Ch341DynLoad.cpp
    src/hal/Ch341Windows.cpp
    src/hal/Ch341LibUsb.cpp
    src/hal/SpiDevBus.cpp
    src/hal/Sx126x.cpp
    src/hal/Sx1262Radio.cpp
    src/dsp/Fir.cpp
    src/dsp/Fft.cpp
    src/dsp/SignalStats.cpp
    src/dsp/Spectrum.cpp
    src/dsp/Resampler.cpp
    src/modem/LoraModem.cpp
    src/modem/ChirpChatRx.cpp
    src/modem/MeshtasticRx.cpp
    src/modem/LoraDecoder.cpp
    src/modem/LoraEncoder.cpp
    src/modem/ChirpChatTx.cpp
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
# No libsodium: it was linked here best-effort, but nothing ever included it
# and MRF_HAVE_SODIUM was never read. Channel crypto needs a generic
# AES-128/256-CTR primitive, which libsodium does not expose (see the WIN32
# branch below), so it was only ever dead weight -- and it is the port that
# failed to build on Linux and macOS, blocking those releases entirely.

find_package(spdlog CONFIG QUIET)
if(spdlog_FOUND)
    target_link_libraries(mrf_core PUBLIC spdlog::spdlog)
    target_compile_definitions(mrf_core PUBLIC MRF_HAVE_SPDLOG=1)
endif()

if(WIN32)
    # AES-CTR for channel crypto is implemented via Windows CNG (bcrypt.lib),
    # since libsodium does not expose a generic AES-128/256-CTR primitive.
    target_link_libraries(mrf_core PUBLIC bcrypt)
else()
    # Non-Windows: use mbedtls's AES-ECB primitive to build the same CTR
    # construction (mbedtls is also what the Meshtastic firmware itself uses
    # for CryptoEngine, so this keeps behavior consistent across platforms).
    find_package(MbedTLS CONFIG REQUIRED)
    target_link_libraries(mrf_core PUBLIC MbedTLS::mbedcrypto)
endif()

# Radio backends are loaded at runtime via LoadLibrary (see hal/*DynLoad.cpp).
# Runtime DLL staging is handled by native/bridge/CMakeLists.txt.

# CH341 USB-SPI bridge for the SX1262 transmit sticks. On Windows this goes
# through WCH's CH341DLL, loaded at runtime like the other radio backends, so
# there is nothing to link. Everywhere else it speaks the bridge's vendor
# protocol over libusb; without libusb the backend still compiles but reports
# itself unavailable, so a missing dependency costs the SX1262 device rather
# than the whole build.
if(NOT WIN32)
    find_package(PkgConfig QUIET)
    if(PkgConfig_FOUND)
        pkg_check_modules(MRF_LIBUSB QUIET libusb-1.0)
    endif()
    if(MRF_LIBUSB_FOUND)
        target_include_directories(mrf_core PRIVATE ${MRF_LIBUSB_INCLUDE_DIRS})
        target_link_libraries(mrf_core PUBLIC ${MRF_LIBUSB_LINK_LIBRARIES})
        target_compile_definitions(mrf_core PRIVATE MRF_HAVE_LIBUSB=1)
    else()
        message(STATUS
            "mrf_core: libusb-1.0 not found; SX1262 USB transmit sticks will be unavailable")
    endif()
endif()

set_target_properties(mrf_core PROPERTIES
    POSITION_INDEPENDENT_CODE ON
    FOLDER "native"
)
