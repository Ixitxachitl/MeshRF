include(FetchContent)

find_package(GTest CONFIG QUIET)
if(NOT GTest_FOUND)
    message(STATUS "tests/native: GTest not found via vcpkg; fetching")
    FetchContent_Declare(
        googletest
        GIT_REPOSITORY https://github.com/google/googletest.git
        GIT_TAG v1.15.2
    )
    set(gtest_force_shared_crt ON CACHE BOOL "" FORCE)
    FetchContent_MakeAvailable(googletest)
endif()

add_executable(mrf_core_tests
    test_packet_header.cpp
    test_channel_hash.cpp
    test_flooding_router.cpp
    test_preset.cpp
    test_dsp_fir.cpp
    test_dsp_dc.cpp
    test_dsp_fft.cpp
    test_dsp_spectrum.cpp
    test_chirp_chat_rx.cpp
    test_lora_decoder.cpp
    test_meshtastic_rx_frame.cpp
    test_lora_tx.cpp
)

target_link_libraries(mrf_core_tests
    PRIVATE
        mrf_core
        $<IF:$<TARGET_EXISTS:GTest::gtest_main>,GTest::gtest_main,gtest_main>
)

include(GoogleTest)
gtest_discover_tests(mrf_core_tests)

set_target_properties(mrf_core_tests PROPERTIES FOLDER "tests")
