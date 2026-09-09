# Export a project bundle

Use **File → Export project bundle… → Download ZIP** to share the current map with its palettes and textures. The ZIP contains all rooms, including edits you have not saved to a named map yet.

The dialog shows progress and lets you cancel. Downloading a bundle does not change the open map, its save status or Undo history. The browser or Windows application's download settings choose where the ZIP is saved.

## What the ZIP contains

```text
Maps/
  world.map.json
.studio/
  catalog.json
Textures/
  ...referenced images...
readme.txt
```

- **world.map.json** contains the complete current map: room IDs, positions, both tile layers, objects, descriptions, minimap properties and camera settings.
- **catalog.json** contains the workspace's palette groups, palettes, autotile rules and object definitions.
- **Textures** contains images referenced by those definitions or the map's backdrops. Both generated atlases and original 4-tile / 47-tile source PNGs are included. Images supplied by the application are copied into the bundle too.

All registered palettes are retained so you can continue painting after moving the project. Other saved maps, unrelated files, recovery data and scripts are not bundled. Resource paths are relative to the extracted folder; the catalog's machine-specific project path is replaced with `.`. Custom map properties remain unchanged.

## Continue editing on another computer

Extract into a new folder and start the studio with that folder as its workspace. For example, from a Windows source checkout, if the extracted folder is named `world-bundle`:

```bat
platform\win\web\run.bat --project ".\world-bundle"
```

The `--project` option also works with the other platform launchers and the Windows program. Use the extracted folder's location as its value. The studio opens the exported map and resolves palettes from the bundled catalog.

Opening only `world.map.json` in an already running, different workspace keeps that workspace's palettes. To use everything in the bundle together, select the extracted folder with `--project`. ZIP import is not a separate menu action in this version.

## Use it with an engine

Extract the ZIP before using your engine package's JSON import. Select:

| Import setting | Choose |
| --- | --- |
| Map JSON | `Maps/world.map.json` |
| Catalog | `.studio/catalog.json` |
| Resource root | The extracted folder containing `Maps`, `.studio` and `Textures` |

The map and catalog retain their existing JSON formats. No engine-package update is required for this bundle layout.

## If export cannot finish

A missing or unreadable image stops the export and shows its resource name. Finish saving that image or fix its palette reference, then retry. If an image or palette changes during export, retry after the change finishes.

Bundles support up to 256 MiB before compression, 32 MiB per image and 4096 images. Names that would collide or be invalid on another supported operating system must be corrected before exporting.
