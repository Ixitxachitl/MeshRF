cmake_minimum_required(VERSION 3.25)

# Build this early so we can decide toolchain setup before project().
option(MRF_BUILD_RADIO_DLLS_FROM_SUBMODULES
    "Build hackrf/rtl-sdr runtime DLLs from third_party submodule sources"
    ON)

# When building radio DLLs from submodules, ensure a vcpkg toolchain exists.
# If none is provided, auto-provision a repo-local .vcpkg checkout.
if(MRF_BUILD_RADIO_DLLS_FROM_SUBMODULES AND WIN32)
    set(_mrf_vcpkg_root "${CMAKE_SOURCE_DIR}/.vcpkg")
    set(_mrf_vcpkg_exe "${_mrf_vcpkg_root}/vcpkg.exe")
    set(_mrf_vcpkg_toolchain "${_mrf_vcpkg_root}/scripts/buildsystems/vcpkg.cmake")

    set(_mrf_using_repo_vcpkg_toolchain OFF)
    if(DEFINED CMAKE_TOOLCHAIN_FILE)
        cmake_path(NORMAL_PATH CMAKE_TOOLCHAIN_FILE OUTPUT_VARIABLE _mrf_cmake_toolchain_norm)
        cmake_path(NORMAL_PATH _mrf_vcpkg_toolchain OUTPUT_VARIABLE _mrf_repo_toolchain_norm)
        if(_mrf_cmake_toolchain_norm STREQUAL _mrf_repo_toolchain_norm)
            set(_mrf_using_repo_vcpkg_toolchain ON)
        endif()
    endif()

    if(NOT DEFINED CMAKE_TOOLCHAIN_FILE)
        find_program(MRF_GIT_EXECUTABLE git)
        if(NOT MRF_GIT_EXECUTABLE)
            message(FATAL_ERROR
                "MRF_BUILD_RADIO_DLLS_FROM_SUBMODULES=ON requires vcpkg auto-provisioning, but git was not found.")
        endif()

        if(NOT EXISTS "${_mrf_vcpkg_root}/.git")
            message(STATUS "MRF: cloning vcpkg into ${_mrf_vcpkg_root}")
            execute_process(
                COMMAND "${MRF_GIT_EXECUTABLE}" clone --depth 1 https://github.com/microsoft/vcpkg "${_mrf_vcpkg_root}"
                RESULT_VARIABLE _mrf_vcpkg_clone_rc
            )
            if(NOT _mrf_vcpkg_clone_rc EQUAL 0)
                message(FATAL_ERROR "MRF: failed to clone vcpkg (exit ${_mrf_vcpkg_clone_rc})")
            endif()
        endif()

        message(STATUS "MRF: bootstrapping local vcpkg")
        execute_process(
            COMMAND "${_mrf_vcpkg_root}/bootstrap-vcpkg.bat"
            WORKING_DIRECTORY "${_mrf_vcpkg_root}"
            RESULT_VARIABLE _mrf_vcpkg_bootstrap_rc
        )
        if(NOT _mrf_vcpkg_bootstrap_rc EQUAL 0)
            message(FATAL_ERROR "MRF: vcpkg bootstrap failed (exit ${_mrf_vcpkg_bootstrap_rc})")
        endif()
    endif()

    if(NOT DEFINED CMAKE_TOOLCHAIN_FILE)
        set(CMAKE_TOOLCHAIN_FILE "${_mrf_vcpkg_toolchain}" CACHE FILEPATH "vcpkg toolchain" FORCE)
        set(_mrf_using_repo_vcpkg_toolchain ON)
    endif()

    if(_mrf_using_repo_vcpkg_toolchain)
        if(NOT DEFINED VCPKG_TARGET_TRIPLET)
            set(VCPKG_TARGET_TRIPLET "x64-windows" CACHE STRING "vcpkg triplet" FORCE)
        endif()

        # Ensure libusb + pthreads are available for upstream HackRF/rtl-sdr submodule builds.
        set(_mrf_libusb_header_probe "${_mrf_vcpkg_root}/installed/${VCPKG_TARGET_TRIPLET}/include/libusb.h")
        set(_mrf_pthread_header_probe "${_mrf_vcpkg_root}/installed/${VCPKG_TARGET_TRIPLET}/include/pthread.h")
        if(NOT EXISTS "${_mrf_libusb_header_probe}" OR NOT EXISTS "${_mrf_pthread_header_probe}")
            execute_process(
                COMMAND "${_mrf_vcpkg_exe}" install --classic libusb:${VCPKG_TARGET_TRIPLET} pthreads:${VCPKG_TARGET_TRIPLET}
                WORKING_DIRECTORY "${_mrf_vcpkg_root}"
                RESULT_VARIABLE _mrf_vcpkg_libusb_rc
            )
            if(NOT _mrf_vcpkg_libusb_rc EQUAL 0)
                message(FATAL_ERROR "MRF: failed to install libusb/pthreads via local vcpkg (exit ${_mrf_vcpkg_libusb_rc})")
            endif()
        endif()

        # PThreads4W's CMake config resolves headers/libs via _VCPKG_INSTALLED_DIR.
        # If artifacts only exist in the package staging tree, mirror them so
        # downstream external projects can find pthread.h reliably.
        set(_mrf_pthreads_pkg_root "${_mrf_vcpkg_root}/packages/pthreads_${VCPKG_TARGET_TRIPLET}")
        set(_mrf_triplet_install_root "${_mrf_vcpkg_root}/installed/${VCPKG_TARGET_TRIPLET}")
        if(EXISTS "${_mrf_pthreads_pkg_root}/include/pthread.h" AND NOT EXISTS "${_mrf_triplet_install_root}/include/pthread.h")
            file(MAKE_DIRECTORY "${_mrf_triplet_install_root}")
            foreach(_mrf_copy_dir include lib bin debug/lib debug/bin)
                if(EXISTS "${_mrf_pthreads_pkg_root}/${_mrf_copy_dir}")
                    file(COPY "${_mrf_pthreads_pkg_root}/${_mrf_copy_dir}" DESTINATION "${_mrf_triplet_install_root}")
                endif()
            endforeach()
        endif()
    endif()
endif()

project(MeshRF
    VERSION 0.0.1
    DESCRIPTION "Windows-native Meshtastic client using HackRF"
    LANGUAGES CXX C
)

include(ExternalProject)

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

if(MRF_BUILD_RADIO_DLLS_FROM_SUBMODULES)
    set(MRF_HACKRF_SUBMODULE_DIR "${CMAKE_SOURCE_DIR}/third_party/hackrf")
    set(MRF_HACKRF_SOURCE_DIR "${MRF_HACKRF_SUBMODULE_DIR}/host")
    set(MRF_RTLSDR_SUBMODULE_DIR "${CMAKE_SOURCE_DIR}/third_party/rtlsdr")

    if(NOT EXISTS "${MRF_HACKRF_SOURCE_DIR}/CMakeLists.txt")
        message(FATAL_ERROR
            "MRF_BUILD_RADIO_DLLS_FROM_SUBMODULES=ON but HackRF submodule sources "
            "are missing at ${MRF_HACKRF_SUBMODULE_DIR}. Initialize/update submodules.")
    endif()
    if(NOT EXISTS "${MRF_RTLSDR_SUBMODULE_DIR}/CMakeLists.txt")
        message(FATAL_ERROR
            "MRF_BUILD_RADIO_DLLS_FROM_SUBMODULES=ON but rtl-sdr submodule sources "
            "are missing at ${MRF_RTLSDR_SUBMODULE_DIR}. Initialize/update submodules.")
    endif()

    set(MRF_HACKRF_INSTALL_DIR "${CMAKE_BINARY_DIR}/_deps/hackrf-install" CACHE INTERNAL "")
    set(MRF_RTLSDR_INSTALL_DIR "${CMAKE_BINARY_DIR}/_deps/rtlsdr-install" CACHE INTERNAL "")

    # Prefer canonical vcpkg installed tree; fall back to package staging tree
    # when classic install artifacts were not integrated yet.
    set(_mrf_vcpkg_triplet_dir "${CMAKE_SOURCE_DIR}/.vcpkg/installed/${VCPKG_TARGET_TRIPLET}")
    if(EXISTS "${_mrf_vcpkg_triplet_dir}/include/libusb-1.0/libusb.h")
        set(_mrf_libusb_include_dir "${_mrf_vcpkg_triplet_dir}/include/libusb-1.0")
        set(_mrf_libusb_lib "${_mrf_vcpkg_triplet_dir}/lib/libusb-1.0.lib")
    elseif(EXISTS "${CMAKE_SOURCE_DIR}/.vcpkg/packages/libusb_${VCPKG_TARGET_TRIPLET}/include/libusb-1.0/libusb.h")
        set(_mrf_libusb_include_dir "${CMAKE_SOURCE_DIR}/.vcpkg/packages/libusb_${VCPKG_TARGET_TRIPLET}/include/libusb-1.0")
        set(_mrf_libusb_lib "${CMAKE_SOURCE_DIR}/.vcpkg/packages/libusb_${VCPKG_TARGET_TRIPLET}/lib/libusb-1.0.lib")
    else()
        message(FATAL_ERROR
            "MRF: libusb headers were not found under .vcpkg/installed or .vcpkg/packages after vcpkg install.")
    endif()

    set(_mrf_pthreads_include_dir "")
    set(_mrf_pthreads_lib "")
    set(_mrf_pthreads_cmake_dir "")
    if(EXISTS "${_mrf_vcpkg_triplet_dir}/include/pthread.h")
        set(_mrf_pthreads_include_dir "${_mrf_vcpkg_triplet_dir}/include")
        if(EXISTS "${_mrf_vcpkg_triplet_dir}/share/PThreads4W/PThreads4WConfig.cmake")
            set(_mrf_pthreads_cmake_dir "${_mrf_vcpkg_triplet_dir}/share/PThreads4W")
        endif()
    elseif(EXISTS "${CMAKE_SOURCE_DIR}/.vcpkg/packages/pthreads_${VCPKG_TARGET_TRIPLET}/include/pthread.h")
        set(_mrf_pthreads_include_dir "${CMAKE_SOURCE_DIR}/.vcpkg/packages/pthreads_${VCPKG_TARGET_TRIPLET}/include")
        if(EXISTS "${CMAKE_SOURCE_DIR}/.vcpkg/packages/pthreads_${VCPKG_TARGET_TRIPLET}/share/PThreads4W/PThreads4WConfig.cmake")
            set(_mrf_pthreads_cmake_dir "${CMAKE_SOURCE_DIR}/.vcpkg/packages/pthreads_${VCPKG_TARGET_TRIPLET}/share/PThreads4W")
        endif()
    endif()

    foreach(_mrf_pthreads_lib_candidate
        "${_mrf_vcpkg_triplet_dir}/lib/pthreadVC3.lib"
        "${_mrf_vcpkg_triplet_dir}/lib/pthreadVCE3.lib"
        "${CMAKE_SOURCE_DIR}/.vcpkg/packages/pthreads_${VCPKG_TARGET_TRIPLET}/lib/pthreadVC3.lib"
        "${CMAKE_SOURCE_DIR}/.vcpkg/packages/pthreads_${VCPKG_TARGET_TRIPLET}/lib/pthreadVCE3.lib")
        if(EXISTS "${_mrf_pthreads_lib_candidate}")
            set(_mrf_pthreads_lib "${_mrf_pthreads_lib_candidate}")
            break()
        endif()
    endforeach()

    set(_mrf_hackrf_libusb_include "${_mrf_libusb_include_dir}")
    if(_mrf_pthreads_include_dir)
        set(_mrf_hackrf_libusb_include "${_mrf_hackrf_libusb_include};${_mrf_pthreads_include_dir}")
    endif()
    set(_mrf_hackrf_libusb_libs "${_mrf_libusb_lib}")
    if(_mrf_pthreads_lib)
        set(_mrf_hackrf_libusb_libs "${_mrf_hackrf_libusb_libs};${_mrf_pthreads_lib}")
    endif()
    set(_mrf_hackrf_cflags "")
    if(_mrf_pthreads_include_dir)
        set(_mrf_hackrf_cflags "/I${_mrf_pthreads_include_dir}")
    endif()
    set(_mrf_hackrf_shared_linker_flags "")
    if(_mrf_pthreads_lib)
        set(_mrf_hackrf_shared_linker_flags "${_mrf_pthreads_lib}")
    endif()

    ExternalProject_Add(mrf_hackrf_vendor
        SOURCE_DIR "${MRF_HACKRF_SOURCE_DIR}"
        BINARY_DIR "${CMAKE_BINARY_DIR}/_deps/hackrf-build"
        INSTALL_DIR "${MRF_HACKRF_INSTALL_DIR}"
        CMAKE_ARGS
            -DCMAKE_TOOLCHAIN_FILE=${CMAKE_TOOLCHAIN_FILE}
            -DVCPKG_TARGET_TRIPLET=${VCPKG_TARGET_TRIPLET}
            -DCMAKE_INSTALL_PREFIX=<INSTALL_DIR>
            -DBUILD_SHARED_LIBS=ON
            -DCMAKE_C_FLAGS=${_mrf_hackrf_cflags}
            -DCMAKE_SHARED_LINKER_FLAGS=${_mrf_hackrf_shared_linker_flags}
            -DLIBUSB_INCLUDE_DIR=${_mrf_hackrf_libusb_include}
            -DLIBUSB_LIBRARIES=${_mrf_hackrf_libusb_libs}
            -DPThreads4W_DIR=${_mrf_pthreads_cmake_dir}
            -DPThreads4W_INCLUDE_DIR=${_mrf_pthreads_include_dir}
            -DPThreads4W_LIBRARY=${_mrf_pthreads_lib}
            -DTHREADS_PTHREADS_INCLUDE_DIR=${_mrf_pthreads_include_dir}
            -DTHREADS_PTHREADS_LIBRARY=${_mrf_pthreads_lib}
        BUILD_COMMAND "${CMAKE_COMMAND}" --build <BINARY_DIR> --config $<CONFIG>
        INSTALL_COMMAND "${CMAKE_COMMAND}" --build <BINARY_DIR> --config $<CONFIG> --target install
        LOG_CONFIGURE ON
        LOG_BUILD ON
        LOG_INSTALL ON
    )

    ExternalProject_Add(mrf_rtlsdr_vendor
        SOURCE_DIR "${MRF_RTLSDR_SUBMODULE_DIR}"
        BINARY_DIR "${CMAKE_BINARY_DIR}/_deps/rtlsdr-build"
        INSTALL_DIR "${MRF_RTLSDR_INSTALL_DIR}"
        CMAKE_ARGS
            -DCMAKE_TOOLCHAIN_FILE=${CMAKE_TOOLCHAIN_FILE}
            -DVCPKG_TARGET_TRIPLET=${VCPKG_TARGET_TRIPLET}
            -DCMAKE_DISABLE_FIND_PACKAGE_PkgConfig=ON
            -DCMAKE_INSTALL_PREFIX=<INSTALL_DIR>
            -DBUILD_SHARED_LIBS=ON
            -DLIBUSB_INCLUDE_DIRS=${_mrf_libusb_include_dir}
            -DLIBUSB_LIBRARIES=${_mrf_libusb_lib}
            -DTHREADS_PTHREADS_INCLUDE_DIR=${_mrf_pthreads_include_dir}
            -DTHREADS_PTHREADS_LIBRARY=${_mrf_pthreads_lib}
        BUILD_COMMAND "${CMAKE_COMMAND}" --build <BINARY_DIR> --config $<CONFIG>
        INSTALL_COMMAND "${CMAKE_COMMAND}" --build <BINARY_DIR> --config $<CONFIG> --target install
        LOG_CONFIGURE ON
        LOG_BUILD ON
        LOG_INSTALL ON
    )
endif()

# ---------------------------------------------------------------------------
# Subprojects
# ---------------------------------------------------------------------------
add_subdirectory(native/core)
add_subdirectory(native/bridge)

if(MRF_BUILD_TESTS)
    enable_testing()
    add_subdirectory(tests/native)
endif()
