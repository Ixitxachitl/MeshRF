cmake_minimum_required(VERSION 3.25)
project(MeshRF
    VERSION 0.0.1
    DESCRIPTION "Windows-native Meshtastic client using HackRF"
    LANGUAGES CXX C
)

# ---------------------------------------------------------------------------
# Global settings
# ---------------------------------------------------------------------------
set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)

if(MSVC)
    add_compile_options(/W4 /permissive- /utf-8 /Zc:__cplusplus)
    add_compile_definitions(_CRT_SECURE_NO_WARNINGS NOMINMAX WIN32_LEAN_AND_MEAN)
endif()

# Output paths so the WPF app can locate the native DLL during dev
set(CMAKE_RUNTIME_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/bin")
set(CMAKE_LIBRARY_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/bin")
set(CMAKE_ARCHIVE_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/lib")

option(MRF_BUILD_TESTS "Build native unit tests" ON)

# Custom find modules (e.g. FindHackRF for libhackrf via HACKRF_DIR hint).
list(APPEND CMAKE_MODULE_PATH "${CMAKE_SOURCE_DIR}/cmake")

# ---------------------------------------------------------------------------
# Subprojects
# ---------------------------------------------------------------------------
add_subdirectory(native/core)
add_subdirectory(native/bridge)

if(MRF_BUILD_TESTS)
    enable_testing()
    add_subdirectory(tests/native)
endif()
