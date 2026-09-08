cmake_minimum_required(VERSION 3.24)
project(StudioNativeTests LANGUAGES CXX)
add_executable(studio-native-tests NativeTests.cpp)
target_compile_features(studio-native-tests PRIVATE cxx_std_17)

add_executable(studio-trigger-tests TriggerTests.cpp)
target_compile_features(studio-trigger-tests PRIVATE cxx_std_17)
