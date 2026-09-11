cmake_minimum_required(VERSION 3.21)
project(PlugBrowserNativeWorker CXX)

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

# The worker talks to plug-ins through NetPlugHost's flat C ABI rather than the VST3 SDK directly, so
# all the hosting logic stays in one place and this stays a thin shell around windowing and capture.
# That also means no SDK headers and no submodule are needed here.
set(NETPLUGHOST_DIR "${CMAKE_CURRENT_SOURCE_DIR}/../../NetPlugHost" CACHE PATH
    "Root of the NetPlugHost repository")

get_filename_component(NETPLUGHOST_DIR "${NETPLUGHOST_DIR}" ABSOLUTE)

if(NOT EXISTS "${NETPLUGHOST_DIR}/native/include/vst3_host_c.h")
    message(FATAL_ERROR
        "NetPlugHost not found at ${NETPLUGHOST_DIR}. "
        "Pass -DNETPLUGHOST_DIR=<path> if it lives elsewhere.")
endif()

# Release is the only configuration the import library is looked for in, because that is what
# NetPlugHost's own build.ps1 produces.
set(NETPLUGHOST_LIB "${NETPLUGHOST_DIR}/native/build/Release/Vst3HostNative.lib")
set(NETPLUGHOST_DLL "${NETPLUGHOST_DIR}/native/build/bin/Vst3HostNative.dll")

if(NOT EXISTS "${NETPLUGHOST_LIB}")
    message(FATAL_ERROR
        "Vst3HostNative.lib not found at ${NETPLUGHOST_LIB}. Build NetPlugHost first:\n"
        "  cmake -S native -B native/build -A x64 && cmake --build native/build --config Release")
endif()

add_executable(PlugBrowser.NativeWorker
    src/main.cpp
    src/capture.cpp
    src/json_writer.cpp
)

target_include_directories(PlugBrowser.NativeWorker PRIVATE "${NETPLUGHOST_DIR}/native/include")

target_link_libraries(PlugBrowser.NativeWorker PRIVATE
    "${NETPLUGHOST_LIB}"
    gdiplus
    shlwapi
    user32
    gdi32
    ole32
)

# x64 only: the VST3 host has no x86 build.
set_target_properties(PlugBrowser.NativeWorker PROPERTIES
    RUNTIME_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/bin"
    RUNTIME_OUTPUT_DIRECTORY_RELEASE "${CMAKE_BINARY_DIR}/bin"
    RUNTIME_OUTPUT_DIRECTORY_DEBUG "${CMAKE_BINARY_DIR}/bin"
)

if(MSVC)
    target_compile_options(PlugBrowser.NativeWorker PRIVATE /W4 /permissive-)
    target_compile_definitions(PlugBrowser.NativeWorker PRIVATE UNICODE _UNICODE)
endif()

# The native half of NetPlugHost has to sit beside the exe for default DLL probing to find it.
add_custom_command(TARGET PlugBrowser.NativeWorker POST_BUILD
    COMMAND ${CMAKE_COMMAND} -E copy_if_different
            "${NETPLUGHOST_DLL}" "$<TARGET_FILE_DIR:PlugBrowser.NativeWorker>"
    COMMENT "Copying Vst3HostNative.dll next to the worker")
