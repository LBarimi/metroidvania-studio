# Headless editing for local agents

A local model with a shell can generate a map, edit it and deliver a project ZIP using the CLI and the .NET 10 runtime. A session runs entirely in the CLI process with stdin and stdout. It uses the same editing code, catalogs, undo history and saves as the web editor. These commands are available in the source build. Run `help` to check an installed version and `editor-help` to read this guide from the installation.

## Generate a playable layout

Create a workspace directory once. From its parent directory, run the following commands with the [CLI built from this checkout](quick-start.md).

From the repository root, replace `metroidvania-studio` in the examples with `node ./bin/metroidvania-studio.mjs` after the Release build. A locally installed package exposes the short command directly.

```sh
mkdir world
metroidvania-studio generate --workspace world --map Maps/World.map.json --rooms 48 --seed 42 --tiles --difficulty progressive
metroidvania-studio validate --workspace world --map Maps/World.map.json
metroidvania-studio preview --workspace world --map Maps/World.map.json --output minimap.svg
```

`generate` creates the file directly. Its reply contains the chosen seed, room count, foreground cell count and saved revision. Omit the seed for a random layout. With `--dry-run`, use the returned seed to repeat the preview when saving. An existing destination requires `--force`; add `--expected-revision HASH` when replacing a map inspected earlier.

| Option | Effect |
| --- | --- |
| `--rooms N` | 1–1,024 rooms, default 64 |
| `--chips 32x16,16x32` or `--exclude-chips 8x8,8x16` | Include or exclude room-chip sizes. The default uses all sizes. See `minimap.place` in `capabilities` for the list. |
| `--colors "#58765c,#c28c53"` | Use these region colors. Omitting the option lets the generator choose the number and colors of regions. |
| `--tiles` | Fill the rooms with editable terrain, ladders and drop-through floors, and place Portal objects on both sides of each passage. |
| `--theme auto` | Match the region color, or supply a terrain material ID from the workspace catalog. Selecting a theme enables tiles. |
| `--difficulty progressive` | Also accepts `easy`, `normal`, or `hard`. Selecting a difficulty enables tiles. |
| `--name "World"` | Set the document name. |

## Edit the generated map

Write the following array to `world/edit.json`. The editor opens the named map, changes the first generated room, undoes and redoes that edit, saves the map, and exports it with its catalog and images.

```json
[
  {"action":"open","path":"World.map.json"},
  {"action":"selectRoom","id":"gen_0000002a_000"},
  {"action":"roomProperties","id":"gen_0000002a_000","name":"Landing"},
  {"action":"undo"},
  {"action":"redo"},
  {"action":"save"},
  {"action":"bundle","output":"World.zip"}
]
```

```sh
metroidvania-studio editor --workspace world --commands edit.json
```

Each completed action prints one JSON line. For a local agent loop, start `editor --workspace world` once, send one compact JSON object per line, flush stdin, and read the reply before choosing the next action. Selection, clipboard, gestures and undo history persist until EOF. Use `--commands -` to pipe one JSON object or a formatted array. A command file is sequential. The first error exits nonzero and earlier completed actions remain applied. Use `apply` or `run` for an atomic document edit with dry-run support.

The editor opens the project's recovery or saved map and holds its workspace writer lock. `open`, `save` and `exportRooms` use paths relative to the project's Maps directory. `--map`, `--commands`, `paletteTemplate.output` and `bundle.output` use paths relative to the workspace root. Use `state` to check the file, dirty flag, selected IDs, revisions and undo availability. Save explicitly when a named file is the deliverable. At EOF, the editor flushes recovery and automatic room exports. When the web editor owns the project, use the [connected CLI](../api/live-api.md) for supported live operations.

## Editor actions

Send the action name in the top-level `action` field. A mutation returns a short receipt. To make a decision conditional on a previous state, pass its `instanceId` as `expectedInstanceId` and its integer `revision` as `expectedRevision` on an editor mutation. The CLI owns the gesture client identity. Tile units increase upward. For portable scripts with stable room IDs, `applyBatch` accepts the [versioned editing operations](../api/operations.md).

| Task | Actions and inputs |
| --- | --- |
| Inspect | `state` (add `"full":true` for map JSON), `inspect` (room IDs and connections), `catalog` (materials, object definitions and palette groups), `files`, `queryTiles` (roomId, layer, x, y, width, height) |
| Map files | `new`, `sampleWorld`, `open` (path), `import` (document), `importRooms` (documents), `save` (optional path), `exportRooms` (scope, directory, overwrite), `bundle` (new ZIP output). Replacing unsaved work requires `discard:true`. `browserOpen` and `browserSave` are the web file-handle bookkeeping actions. Use `open` and `save` for filesystem I/O. |
| Generate in the open session | `generateMinimap` takes roomCount, seed, chipIds, colors and automaticThemes. Add generateTiles, tileTheme and tileDifficulty for terrain. Use `save` afterwards. |
| Room chips and rooms | `roomAdd` (x, y, width, height), `selectRoom` (id, optional toggle), `roomMove`, `roomResize`, `roomProperties`, `roomResolutionFit`, `roomFitResolution`, `roomDelete`, `roomDeleteSelected`, `roomDuplicate`, `roomCopy`, `roomCut`, `roomPaste`, `roomFlip`, `roomRotate`, `roomMerge`, `roomSplit`. For grid-snapped chips use `minimap.place` in `applyBatch`. |
| Tile tools and layers | `options` selects tool, layer, shape, material, brushSize, filled, groupId, hiddenLayers or lockedLayers. `pick`, `begin`, `drag`, `end` use x and y. `cancel` ends a gesture. `tileGesture` and `objectGesture` apply a point list with the editor's gesture preconditions. `clearLayer` and `clearObjects` use the current room. |
| Selection and history | `selectArea` (x, y, width, height), `selectAll`, `moveSelection` (dx, dy), `copy`, `cut`, `paste` (x, y), `delete`, `flip` (horizontal), `rotate` (clockwise), `undo`, `redo` |
| Objects, triggers and paths | `objectClick` (x, y, additive), `objectMove` and `objectResize` (dx, dy), `objectTransform`, `objectProperties` (values), `nodeSelect` (id, index), `nodeAdd`, `nodeMove` (x, y), `nodeDelete`. Use a definition ID from `catalog`. |
| Tilesets | `paletteAdd`, `paletteConfigure` (including atlas or separate PNG uploads), `paletteGroupAdd`, `paletteGroupMove`, `paletteMove`, `paletteTemplate` (mode `four` or `blob47`, color, new PNG output). Inspect `catalog` before editing an existing material or group order. |
| Properties and camera | `documentProperties` (name, properties, stylegrounds or layerGroups), `cameraSettings` (ppu, referenceWidth, referenceHeight) |
| Scripts | `scripts`, `readScript` (id), `saveScript` (name, source, expectedRevision or null for a new file), `runLua` (source, optional seed and dryRun), `applyBatch` (batch, optional dryRun) |

Map and catalog authoring run headlessly. Browser zoom, pan, animated generation, the Game Preview viewport and native file dialogs are presentation controls. The CLI's `preview` renders an SVG of the room layout. Use `queryTiles` to inspect terrain and `bundle` to deliver textured maps.
