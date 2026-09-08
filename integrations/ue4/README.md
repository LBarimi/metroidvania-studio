# Unreal Engine 4 integration

Installation guides: [한국어](../../engine-packages/ue4/INSTALL_KR.txt) · [English](../../engine-packages/ue4/INSTALL_EN.txt) · [日本語](../../engine-packages/ue4/INSTALL_JP.txt) · [简体中文](../../engine-packages/ue4/INSTALL_CN.txt) · [繁體中文](../../engine-packages/ue4/INSTALL_TW.txt).

Developed for Unreal Engine 4.27. The plugin uses the shared Unreal room loader and pixel camera.

1. Extract `engine-packages/ue4/metroidvania-studio.zip` into a temporary folder.
2. Close the target editor. Double-click `install.bat` and select the `.uproject`. The installer finds the matching engine automatically. If needed, pass `-EngineRoot` to `Install-Integration.ps1` or set `UNREAL_ENGINE4_PATH`. The C++ toolchain required by the engine must be installed.
3. Open the project and place a **MetroidvaniaStudioRoom** actor in the level. In Details > Metroidvania Studio > Import, select **Map File**, **Catalog File**, **Resource Directory**, and optionally enter a stable **Room Id**. Empty ID selects the first room. Click **Import Room**.
4. Save the level. Imported JSON and PNG bytes are stored in the actor; runtime reconstruction does not need the original files. **Rebuild Room** reconstructs the embedded data. **Clear Room** removes it. To import newer JSON, select the source files again and click **Import Room**.

The archive carries source, not engine-version-specific DLLs. It preserves other plugins and backs up both the project file and any previous plugin under `.metroidvania-studio-backups`. Linux/macOS users can copy `plugin/MetroidvaniaStudio` into the project's `Plugins` directory and build with that platform's engine toolchain.

Terrain is batched by layer and atlas, uses nearest-filtered unlit textures, and creates convex solid/triangle collision. Trigger geometry generates overlap events; the component tags contain object ID and definition. All original object properties remain available in `MapDocumentJson`. Entities and decorations are visuals/metadata; no game-specific logic is executed. Styleground effects, automatic synchronization and a room dropdown are future adapter work.

Map X maps to Unreal X; map Y maps to Unreal Z. A tile spans `16 / ppu * UnitsPerWorldUnit`, with 100 Unreal units per world unit by default. The supplied orthographic camera uses the reference resolution and PPU. Set the player controller's view target to this actor when testing the room camera. The camera renders to a fixed-resolution texture and presents it through a viewport widget at an integer scale with nearest filtering. Source-pixel camera alignment and disabled temporal effects prevent subpixel shimmer. Unused window space is black; windows below the reference resolution crop at 1x. HUD widgets can render above the pixel view. This camera supports axis-aligned 2D framing. White room outlines appear in the level editor and are hidden in Game View.

Textures resolve relative to the selected resource directory, respecting catalog path casing. The bundled sample is under `plugin/MetroidvaniaStudio/samples`. The importer has no network client. The installer invokes the installed engine's standard local build tools.

Validation: package with the matching engine's BuildPlugin command with `-VS2019`, then run the `MetroidvaniaStudio.Import.Room` automation test using `UE4Editor-Cmd` with `-unattended -nullrhi -nosound`. Run an actual packaged-game build in the consuming project before shipping a game.

## Objects and trigger events

See `TRIGGERS.md` in the package for event requests, one-shot portals, stable IDs and runtime examples. The game decides when to request an event; the integration does not wire collision callbacks.
