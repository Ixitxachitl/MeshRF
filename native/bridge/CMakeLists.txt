add_library(mrf_bridge SHARED
    src/c_api.cpp
)

target_link_libraries(mrf_bridge PRIVATE mrf_core)

target_include_directories(mrf_bridge
    PUBLIC
        $<BUILD_INTERFACE:${CMAKE_CURRENT_SOURCE_DIR}/include>
)

target_compile_definitions(mrf_bridge PRIVATE MRF_BUILDING_BRIDGE=1)

set_target_properties(mrf_bridge PROPERTIES
    OUTPUT_NAME "MeshtasticRF.Native"
    FOLDER "native"
)

# Copy the vendored hackrf runtime (hackrf.dll + transitive deps) next to the
# bridge DLL so LoadLibrary can resolve everything from one directory.
set(_mrf_hackrf_runtime_dir "${CMAKE_SOURCE_DIR}/third_party/hackrf")
foreach(_dll IN ITEMS hackrf.dll libusb-1.0.dll pthreadVC2.dll)
    set(_src "${_mrf_hackrf_runtime_dir}/${_dll}")
    if(EXISTS "${_src}")
        add_custom_command(TARGET mrf_bridge POST_BUILD
            COMMAND ${CMAKE_COMMAND} -E copy_if_different
                    "${_src}"
                    "$<TARGET_FILE_DIR:mrf_bridge>/${_dll}"
            VERBATIM)
    endif()
endforeach()
# Also accept a system-installed hackrf.dll from MRF_HACKRF_DLL as a fallback
# (only if we don't already have a vendored copy).
if(MRF_HACKRF_DLL AND NOT EXISTS "${_mrf_hackrf_runtime_dir}/hackrf.dll")
    add_custom_command(TARGET mrf_bridge POST_BUILD
        COMMAND ${CMAKE_COMMAND} -E copy_if_different
                "${MRF_HACKRF_DLL}"
                "$<TARGET_FILE_DIR:mrf_bridge>/hackrf.dll"
        VERBATIM)
endif()

# Copy the vendored rtlsdr runtime next to the bridge DLL. librtlsdr links
# libusb-1.0.dll, which we already vendor above for hackrf, so the two backends
# share that dependency. We ship our own rtlsdr.dll and never probe for
# third-party SDR installs at runtime.
set(_mrf_rtlsdr_runtime_dir "${CMAKE_SOURCE_DIR}/third_party/rtlsdr")
set(_rtlsdr_src "${_mrf_rtlsdr_runtime_dir}/rtlsdr.dll")
if(EXISTS "${_rtlsdr_src}")
    add_custom_command(TARGET mrf_bridge POST_BUILD
        COMMAND ${CMAKE_COMMAND} -E copy_if_different
                "${_rtlsdr_src}"
                "$<TARGET_FILE_DIR:mrf_bridge>/rtlsdr.dll"
        VERBATIM)
endif()
