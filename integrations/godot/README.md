# Godot integration

Godot 4 addon. Tested with Godot 4.7.2 on Windows using headless import and scene round-trip validation.

1. Extract `engine-packages/godot/metroidvania-studio.zip` into a temporary folder.
2. Close the target editor, double-click `install.bat`, and select `project.godot`. This copies and enables only this addon, preserving other plugins and making a backup.
3. Open the project. In the MetroidvaniaStudio dock select the exported map JSON, `catalog.json`, resource directory and room, then press **Import / Reload room**.

On Linux/macOS copy the archive's `addons/metroidvania-studio` into the project and enable **MetroidvaniaStudio** in Project Settings > Plugins. No executable engine binaries are shipped.

The generated scene embeds atlas textures, terrain geometry, triangle/solid collision, object metadata and trigger areas. Save it under version control with the addon. Reload replaces the generated scene for the same stable room ID; keep gameplay additions in a separate scene that instances it. Hidden layer groups are excluded. Object definitions and properties are metadata, not executable game behavior. Styleground effects and automatic live synchronization are not implemented in this initial adapter.

The map uses +Y up; Godot uses +Y down. One world unit equals `ppu` source pixels. Camera zoom uses PPU, so 16 source pixels occupy 16 screen pixels at native resolution. Set the project's viewport to the imported room's `reference_resolution` metadata (default 320 by 180), use integer window scaling and nearest texture filtering. The importer does not overwrite project display settings.

Runtime API: preload `res://addons/metroidvania-studio/room-loader.gd`, instantiate it and call `load_room(map_path, catalog_path, resource_directory, room_id)`. It returns a `Node2D` or null; read `error` on failure. For exported games, prefer the generated scene: it needs no access to loose JSON or external texture files.

JSON alone does not contain texture pixels. Select the directory relative to which catalog sprite `asset` paths resolve. Names and path casing must match on Linux/macOS. The bundled `samples` directory provides a portable test map and catalog. All import operations are local.

Backups are under the target project's `.metroidvania-studio-backups`. To uninstall, disable the plugin and remove `addons/metroidvania-studio`; restore the backed-up project file if needed. Do not remove the addon while imported scenes still reference its runtime script.
