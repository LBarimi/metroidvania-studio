# Unreal Engine integration

Initial target: Unreal Engine 5. Windows validation uses the installed 5.8.2 toolchain. Unreal Engine 4 support is not included. Other UE5 minor versions require their own compile and import verification.

1. Extract `engine-packages/ue/metroidvania-studio.zip` into a temporary folder.
2. Close the target editor. Double-click `install.bat`, select the `.uproject`, and wait for compilation. `UNREAL_ENGINE_PATH` must point to the matching engine installation; alternatively pass `-EngineRoot` to `Install-Integration.ps1`. The C++ toolchain required by that engine must be installed.
3. Open the project and place a **MetroidvaniaStudioRoom** actor in the level. In Details > MetroidvaniaStudio > Import, select **Map File**, **Catalog File**, **Resource Directory**, and optionally enter a stable **Room Id**. Empty ID selects the first room. Click **Import Room**.
4. Save the level. Imported JSON and PNG bytes are stored in the actor; runtime reconstruction does not need the original files. **Rebuild Room** reconstructs the embedded data. **Clear Room** removes it. To import newer JSON, select the source files again and click **Import Room**.

The archive carries source, not engine-version-specific DLLs. Installation compiles first and updates the project only after success. It preserves other plugins and backs up both the project file and any previous plugin under `.metroidvania-studio-backups`. Linux/macOS users can copy `plugin/MetroidvaniaStudio` into the project's `Plugins` directory and build with that platform's engine toolchain.

Terrain is batched by layer and atlas, uses nearest-filtered unlit textures, and creates convex solid/triangle collision. Trigger geometry generates overlap events; the component tags contain object ID and definition. All original object properties remain available in `MapDocumentJson`. Entities and decorations are visuals/metadata; no game-specific logic is executed. Styleground effects, automatic synchronization and a room dropdown are future adapter work.

Map X maps to Unreal X; map Y maps to Unreal Z. A tile spans `16 / ppu * UnitsPerWorldUnit`, with 100 Unreal units per world unit by default. The supplied orthographic camera uses the reference resolution and PPU. Set the player controller's view target to this actor when testing the room camera. Nearest texture filtering is supplied; a fixed-resolution render target is needed if the game requires strict final-frame integer pixel scaling.

Textures resolve relative to the selected resource directory, respecting catalog path casing. The bundled sample is under `plugin/MetroidvaniaStudio/samples`. The importer has no network client. The installer invokes the installed engine's standard local build tools.

Validation: package with the matching engine's BuildPlugin command, then run the `MetroidvaniaStudio.Import.Room` automation test using `UnrealEditor-Cmd` with `-unattended -nullrhi -nosound`. Run an actual packaged-game build in the consuming project before shipping a game.
