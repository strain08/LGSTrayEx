# Set the minimum required version of CMake
cmake_minimum_required(VERSION 3.14)

project(hidapi)

# Include the FetchContent module
include(FetchContent)

# Define the external dependency (e.g., Google Test for demonstration)
FetchContent_Declare(
  hidapi
  GIT_REPOSITORY https://github.com/libusb/hidapi.git
  GIT_TAG        19112a2 # connection-callback
)

FetchContent_MakeAvailable(hidapi)

target_link_options(hidapi_winapi PRIVATE "/Brepro")
target_link_options(hidapi_winapi PRIVATE "/INCREMENTAL:NO")

set_target_properties(hidapi_winapi PROPERTIES
    RUNTIME_OUTPUT_DIRECTORY_RELEASE  "${CMAKE_SOURCE_DIR}"
)
