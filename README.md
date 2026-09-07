# <img src="metroidvania-studio/web/studio-icon.svg" width="36" height="36" alt=""> Metroidvania Studio

**Build connected worlds, one room at a time.**

A web-based 2D world editor for metroidvania games. Create connected rooms, paint tilemaps, and design minimaps with JSON export for cross-engine workflows.

Runs locally on Windows, macOS, and Linux. No game engine installation is required to use the studio.

## Add Rooms & Paint Tiles

![Add a second room, paint its tiles, then pan back and paint the first room to connect their terrain](media/readme/create-and-paint.gif)

## Resize, Rotate & Connect Rooms

![Resize and rotate a room, then drag two separate rooms into place to connect them](media/readme/resize-rotate-and-connect.gif)

## Design Your Minimap

![Explore the minimap, double-click a room to edit it, then zoom out](media/readme/design-the-minimap.gif)

## Get Started

Download a ready-to-run package from [GitHub Releases](https://github.com/LBarimi/metroidvania-studio/releases/latest), extract it, and open the launch file at the top level.

To build from source, install **Node.js 24+**, **.NET SDK 10**, and **Git**.

| Platform | Build | Run |
| --- | --- | --- |
| Windows | `platform/win/web/build.bat` | `platform/win/web/run.bat` |
| macOS | `platform/mac/build.command` | `platform/mac/run.command` |
| Linux | `bash platform/linux/build.sh` | `bash platform/linux/run.sh` |

**Run** opens the studio in your browser and builds it on first launch if needed. After updating the source, use **Build**, then **Run**. Prebuilt web copies require **ASP.NET Core Runtime 10**.

Release downloads keep maps in your user data folder. Source builds use `.local/workspace/Maps`. Room JSON exports update automatically in `Maps/AutoExport` inside the workspace. Keep your workspace when updating the studio.

## Editing Basics

- **Paint:** left-drag inside the active room. The first click on another room selects it.
- **Erase:** right-drag inside the active room.
- **Pan and zoom:** middle-drag to pan, mouse wheel to zoom, **Ctrl + wheel** to change brush size. Right-drag on empty space also pans.
- **Arrange rooms:** right-click empty space to add a room, drag a room's title strip to move it, or drag its outer handles to resize. Rotation is available in the inspector.
- **Save and exchange maps:** use **File** for saving and JSON import/export. Use **Edit** for Undo/Redo and camera settings (default PPU **16**, resolution **320×180**).

See **Help → Shortcuts** for the full control list. The interface follows your browser language and can be changed from the language picker.

## Scripts & Automation

Use **Edit → Lua Scripts** to generate rooms or repeat editing tasks. Run the same operations headlessly through the **CLI**, or connect an **AI agent** through the local **MCP server**. Dry runs, atomic updates, and revision checks keep scripted edits reviewable.

Start with the [automation guides](docs/index.md), [Lua examples](docs/examples/connected-rooms.lua), or [MCP setup](docs/mcp/setup.md). The 1.1.0 npm and MCP packages are being prepared; local packaging is available for testing.

## Engine Packages

| Engine | Package | Installation |
| --- | --- | --- |
| Unity | [.unitypackage](engine-packages/unity/metroidvania-studio.unitypackage) | [Guide](engine-packages/unity/INSTALL_EN.txt) |
| Godot | [Addon ZIP](engine-packages/godot/metroidvania-studio.zip) | [Guide](engine-packages/godot/INSTALL_EN.txt) |
| Unreal Engine 4 | [Plugin ZIP](engine-packages/ue4/metroidvania-studio.zip) | [Guide](engine-packages/ue4/INSTALL_EN.txt) |
| Unreal Engine 5 | [Plugin ZIP](engine-packages/ue5/metroidvania-studio.zip) | [Guide](engine-packages/ue5/INSTALL_EN.txt) |
| SDL3 | [C++ source ZIP](engine-packages/sdl/metroidvania-studio.zip) | [Guide](engine-packages/sdl/INSTALL_EN.txt) |

Each engine folder includes translated installation guides. Maps use a shared [JSON format](metroidvania-studio/contracts/FORMAT.md); engine packages handle importing and rendering, while gameplay stays in your game project.

## License

Project-owned code and assets use the [MIT License](LICENSE).
See [Third-party notices](THIRD-PARTY-NOTICES.md) for external components and their terms.
