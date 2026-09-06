# SDL integration

C++17 source library and room preview for SDL 3.4 or later. This is a library integration, so there is no engine plugin manager to install into.

1. Extract `engine/sdl/metroidvania-studio.zip`.
2. Install CMake 3.24+ and a C++17 compiler. On Windows run `build.bat`; it also detects the CMake bundled with a C++ installation of Visual Studio. On Linux/macOS run `sh build.sh`.
3. Double-click `run.bat` (or run `sh run.sh`) to preview the bundled sample. On Windows `run.bat` builds automatically on first use. Arrow keys move the camera; R reloads the same room; Escape closes it.

To use your export, run the preview with `<map.json> <catalog.json> <resource-directory> [room-id]`. An empty room ID selects the first room. The catalog and its relative texture paths are required in addition to JSON. Test mode `--smoke` renders into a memory surface and validates a nonempty frame without opening a window.

Build finds an installed SDL package first. If unavailable it downloads the pinned SDL 3.4.16 source from its official distribution site into the build cache. `-DSTUDIO_FETCH_SDL=OFF -DSDL3_DIR=...` requires your existing installation and disables downloading. No JSON, textures or other project data is sent to a server; only a dependency download occurs at build time. Preserve SDL's mandatory notices when distributing a linked executable.

For your own application, add this folder with `add_subdirectory`, link `metroidvania-studio-sdl`, include `StudioSdlRoom.h`, and construct `MetroidvaniaStudio::SdlRoom` with your `SDL_Renderer`. Call `Load(map,catalog,resources,room_id)` once and `Draw(camera_world_position,width,height)` each frame. A failed reload retains the previous room. Destroy the room before its renderer.

The loader resolves eight-neighbor autotiles, boundary continuation, diagonal geometry, groups, object transforms and camera overrides. Texture geometry is batched by layer and atlas. `data.primitives` exposes foreground collision polygons and trigger polygons; SDL has no physics engine, so connect these to your own collision system. Full map/object metadata stays in `data.document` and `data.metadata`. Game-specific behavior, stylegrounds and automatic file watching are outside this initial adapter.

Coordinates use +Y up in room-local source pixels. One world unit equals `data.ppu` pixels. The renderer converts to SDL's downward Y axis and snaps the camera to source pixels. The preview uses the exported reference resolution with integer presentation and nearest texture sampling.

Windows compilation and a software-rendered frame are validated. Linux/macOS launch scripts are supplied; native builds on those hosts are still required before claiming platform validation.
