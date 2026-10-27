cmake_minimum_required(VERSION 3.20)
project(KanataWarutsuDsp VERSION 1.0.0 LANGUAGES CXX)

option(KW_DSP_BUILD_TESTS "Build native DSP tests" ON)

add_library(KanataWarutsuDsp SHARED src/kanata_warutsu_dsp.cpp)
target_include_directories(KanataWarutsuDsp PUBLIC ${CMAKE_CURRENT_SOURCE_DIR}/include)
target_compile_features(KanataWarutsuDsp PUBLIC cxx_std_17)
target_compile_definitions(KanataWarutsuDsp PRIVATE KW_DSP_BUILD)
set_target_properties(KanataWarutsuDsp PROPERTIES
    CXX_VISIBILITY_PRESET hidden
    VISIBILITY_INLINES_HIDDEN YES
    VERSION ${PROJECT_VERSION}
    SOVERSION 1)
if(MSVC)
  # Keep the Windows plugin backend self-contained; don't require users to install the
  # matching MSVC runtime separately.
  set_property(TARGET KanataWarutsuDsp PROPERTY MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")
endif()
if(MSVC)
  target_compile_options(KanataWarutsuDsp PRIVATE /W4 /permissive- /EHsc)
else()
  target_compile_options(KanataWarutsuDsp PRIVATE -Wall -Wextra -Wpedantic -Wconversion -Wshadow -fexceptions)
endif()

if(KW_DSP_BUILD_TESTS AND NOT ANDROID)
  enable_testing()
  add_executable(KanataWarutsuDspTests tests/test_engine.cpp)
  target_link_libraries(KanataWarutsuDspTests PRIVATE KanataWarutsuDsp)
  target_compile_features(KanataWarutsuDspTests PRIVATE cxx_std_17)
  add_test(NAME dsp-tests COMMAND KanataWarutsuDspTests)
endif()
