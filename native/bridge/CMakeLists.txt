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
    OUTPUT_NAME "MeshRF.Native"
    FOLDER "native"
)

if(MRF_BUILD_RADIO_DLLS_FROM_SUBMODULES)
    add_dependencies(mrf_bridge mrf_hackrf_vendor mrf_rtlsdr_vendor)

    # Copy all installed runtime DLLs built from upstream submodule sources.
    add_custom_command(TARGET mrf_bridge POST_BUILD
    COMMAND ${CMAKE_COMMAND} -E copy_directory
        "${MRF_HACKRF_INSTALL_DIR}/bin"
        "$<TARGET_FILE_DIR:mrf_bridge>"
    VERBATIM)
    add_custom_command(TARGET mrf_bridge POST_BUILD
    COMMAND ${CMAKE_COMMAND} -E copy_directory
        "${MRF_RTLSDR_INSTALL_DIR}/bin"
        "$<TARGET_FILE_DIR:mrf_bridge>"
    VERBATIM)
endif()
