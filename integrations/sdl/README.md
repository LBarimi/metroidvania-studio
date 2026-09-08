# SDL integration

Installation guides: [한국어](../../engine-packages/sdl/INSTALL_KR.txt) · [English](../../engine-packages/sdl/INSTALL_EN.txt) · [日本語](../../engine-packages/sdl/INSTALL_JP.txt) · [简体中文](../../engine-packages/sdl/INSTALL_CN.txt) · [繁體中文](../../engine-packages/sdl/INSTALL_TW.txt).

C++17 source library and room preview for SDL 3.4 or later. This is a library integration, so there is no engine plugin manager to install into.

1. Extract `engine-packages/sdl/metroidvania-studio.zip` in full.
2. Install CMake 3.24+ and a C++17 compiler. Windows also supports Visual Studio with the C++ desktop workload and CMake tools.
3. Double-click `run.bat` on Windows; it builds on first use. On Linux/macOS run `sh build.sh`, then `sh run.sh`. Arrow keys move the camera, R reloads, and Escape closes the preview.

## Read the sample

The preview builds directly from `src-sample`. The same source files are provided beside the ZIP in `engine-packages/sdl/src-sample`, so they can be read before extraction. Build the copy inside the complete extracted package.

| File | Start here to understand |
| --- | --- |
| `src-sample/sample-room.cpp` | Loading JSON and textures, centering the camera, drawing each frame |
| `src-sample/sample-room.h` | Input paths and the small reusable `SampleRoom` class |
| `src-sample/main.cpp` | Creating SDL, handling input, running the loop and releasing resources |

Start with `Reload()` and `Draw()` in `sample-room.cpp`: these call `SdlRoom::Load` and `SdlRoom::Draw`. After editing the sample, run `build.bat` then `run.bat`, or `sh build.sh` then `sh run.sh`. The sample relies on the package's `include`, `src`, `samples` and `CMakeLists.txt`.

To use your export, run the preview with `<map.json> <catalog.json> <resource-directory> [room-id]`. An empty room ID selects the first room. The catalog and its relative texture paths are required in addition to JSON. Test mode `--smoke` renders into a memory surface and validates a nonempty frame without opening a window.

Build finds an installed SDL package first. If unavailable it downloads the pinned SDL 3.4.16 source from its official distribution site into the build cache. `-DSTUDIO_FETCH_SDL=OFF -DSDL3_DIR=...` requires your existing installation and disables downloading. No JSON, textures or other project data is sent to a server; only a dependency download occurs at build time. Preserve SDL's mandatory notices when distributing a linked executable.

## Use it in your application

Copy the full extracted package to `third-party/metroidvania-studio`. Copy `sample-room.h` and `sample-room.cpp` into your own `src` folder; use `main.cpp` as a reference for your existing loop. After declaring your executable target, add:

```cmake
add_subdirectory(third-party/metroidvania-studio)
target_sources(my_app PRIVATE src/sample-room.cpp)
target_link_libraries(my_app PRIVATE metroidvania-studio-sdl)
```

Replace `my_app` with your target name. Include `sample-room.h`, construct `MetroidvaniaStudio::Sample::SampleRoom` after your renderer, and call `Draw(width,height)` each frame. Destroy the room before its renderer. The standalone sample builds by default when this package is the top-level CMake project; it is disabled by default when added to another project. Set `STUDIO_BUILD_SAMPLE` explicitly to override this.

You can also use `MetroidvaniaStudio::SdlRoom` directly through `StudioSdlRoom.h`: call `Load(map,catalog,resources,room_id)` once and `Draw(camera_world_position,width,height)` each frame. A failed reload retains the previous room.

The loader resolves eight-neighbor autotiles, boundary continuation, diagonal geometry, groups, object transforms and camera overrides. Texture geometry is batched by layer and atlas. `data.primitives` exposes foreground collision polygons and trigger polygons; SDL has no physics engine, so connect these to your own collision system. Full map/object metadata stays in `data.document` and `data.metadata`. Game-specific behavior, stylegrounds and automatic file watching are outside this initial adapter.

Coordinates use +Y up in room-local source pixels. One world unit equals `data.ppu` pixels. The renderer converts to SDL's downward Y axis and snaps the camera to source pixels. The preview uses the exported reference resolution with integer presentation and nearest texture sampling. `SampleRoom::Draw(width, height)` includes a white room outline; pass `false` as its third argument for game rendering. `SdlRoom::Draw` does not add an outline.

Windows compilation and a software-rendered frame are validated. Linux/macOS launch scripts are supplied; native builds on those hosts are still required before claiming platform validation.

## Objects and trigger events

See `TRIGGERS.md` in the package for event requests, one-shot portals, stable IDs and runtime examples. The game decides when to request an event; the integration does not wire collision callbacks.
