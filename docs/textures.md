# Texture editing and engine resources

## Edit an existing tileset

Save or export the image to the same PNG file registered in the workspace catalog. The studio notices the changed file and updates the palette and map view automatically, usually within one or two seconds. Saving a layered source document alone does not update its exported PNG.

Tile positions, tile shapes and Undo history stay intact. Keep the atlas dimensions and sprite positions unchanged when redrawing a tileset. Changing the atlas layout also requires updating its sprite rectangles in the catalog.

The workspace catalog is normally `.studio/catalog.json`. Each sprite's `asset` identifies its image, for example `Textures/palettes/palette-id.png`. Newly added palettes use PNGs in the workspace's `Textures/palettes/` directory. Workspace settings may configure another texture root. Edit the registered workspace image; changing a separate copy will not update it. The bundled sample images are fallbacks until a matching workspace image is provided.

Checks run in the background and are based on unique image files, independent of painted tile count. Incomplete or locked saves are retried, and the browser retains the previous image while a replacement loads. A file whose timestamp and length were preserved is also checked periodically; these changes can take several additional seconds.

## Share one source with an engine

Use the studio workspace as the shared source folder. Edit each PNG there and configure the engine adapter with that same catalog and resource root. This keeps one editable original; engine-generated assets are derived copies for rendering and packaging. The engine package remains a reusable loader, separate from your project data. Every adapter reads the same three parts:

| File | Contents |
| --- | --- |
| Map or room JSON | Room layout, tile material IDs, shapes and objects |
| Matching catalog JSON | Material IDs, image paths and sprite rectangles |
| Image files | The pixels referenced by the catalog |

For file-based import, select the workspace catalog, exported map/rooms and resource root directly. A shared workspace can look like this:

```text
world/
  Maps/
    room-01.json
  .studio/
    catalog.json
  Textures/
    palettes/
      palette-id.png
```

Choose `world/` as the resource root and `world/.studio/catalog.json` as the catalog in the adapter. Preserve the exact case of paths in the catalog. For a shared file-based workspace, keep the default `Textures/` layout so every loader resolves the same originals. When handing a project to another machine, include all referenced images, including any bundled fallback images, at their catalog paths. A map JSON alone does not contain texture pixels or the sprite mapping. Adding or redrawing a tileset does not require rebuilding the engine package.

The Unity connection window already downloads catalog textures from the running studio. With **Follow web edits** enabled, the texture revision also requests a resource refresh. For the other adapters, select the matching exported files and use their import/reload action again. Automatic live synchronization is currently provided by the Unity adapter only.
