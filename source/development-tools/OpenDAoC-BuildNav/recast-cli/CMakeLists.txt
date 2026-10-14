cmake_minimum_required(VERSION 3.16)
project(recast_cli CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
if(NOT CMAKE_BUILD_TYPE)
  set(CMAKE_BUILD_TYPE Release)
endif()

# Static link gcc libs so the binary runs anywhere on this box.
if(UNIX AND NOT APPLE)
  set(CMAKE_CXX_FLAGS "${CMAKE_CXX_FLAGS} -O2 -static-libstdc++ -static-libgcc")
  find_package(OpenMP REQUIRED)
endif()

# 64-bit poly refs: must match the server's Detour (OpenDAoC-Core builds with DT_POLYREF64)
# and the original Windows RecastDemo.exe that produced the shipped navmeshes.
add_compile_definitions(DT_POLYREF64)

set(RECAST_SRC
  third_party/recast/src/Recast.cpp
  third_party/recast/src/RecastAlloc.cpp
  third_party/recast/src/RecastArea.cpp
  third_party/recast/src/RecastContour.cpp
  third_party/recast/src/RecastFilter.cpp
  third_party/recast/src/RecastLayers.cpp
  third_party/recast/src/RecastMesh.cpp
  third_party/recast/src/RecastMeshDetail.cpp
  third_party/recast/src/RecastRasterization.cpp
  third_party/recast/src/RecastRegion.cpp
  third_party/recast/src/RecastAssert.cpp
  third_party/recast/src/ChunkyTriMesh.cpp
)

set(DETOUR_SRC
  third_party/detour/src/DetourAlloc.cpp
  third_party/detour/src/DetourAssert.cpp
  third_party/detour/src/DetourCommon.cpp
  third_party/detour/src/DetourNavMesh.cpp
  third_party/detour/src/DetourNavMeshBuilder.cpp
  third_party/detour/src/DetourNavMeshQuery.cpp
  third_party/detour/src/DetourNode.cpp
)

add_executable(recast_cli recast_cli.cpp ${RECAST_SRC} ${DETOUR_SRC})
target_include_directories(recast_cli PRIVATE
  third_party/recast/include
  third_party/detour/include
)
if(UNIX AND NOT APPLE)
  target_link_libraries(recast_cli PRIVATE OpenMP::OpenMP_CXX)
endif()

# Drop into base/ next to RecastDemo.exe so NavmeshMgr picks it up unchanged.
add_custom_command(TARGET recast_cli POST_BUILD
  COMMAND ${CMAKE_COMMAND} -E copy $<TARGET_FILE:recast_cli> ${CMAKE_BINARY_DIR}/RecastDemo.exe
)
