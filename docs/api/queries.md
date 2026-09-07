# Tile queries

Read a bounded rectangle of existing terrain before preparing an edit. Tile queries never alter the map or its revision.

```sh
metroidvania-studio query-tiles --workspace workspace --map maps/world.json --room hall --layer foreground --x 0 --y 0 --width 24 --height 2
```

The CLI result contains the map `revision` and a `query` object. `studio_query_tiles` provides the same query through MCP. Connected mode accepts `--url` and `--map @active`.

```json
{
  "roomId": "hall",
  "layer": "foreground",
  "x": 0,
  "y": 0,
  "width": 2,
  "height": 1,
  "cells": [
    { "x": 0, "y": 0, "shape": "solid", "materialId": "terrain", "groupId": "" }
  ]
}
```

Coordinates are local to the room. The rectangle must be inside the room, with positive dimensions and an area no larger than 4096 tiles. Supported layers are `foreground` and `background`. Empty coordinates are omitted. Results are ordered by Y, then X; cell coordinates remain room-local rather than relative to the requested rectangle.

C# callers can use `AutomationEngine.QueryTiles(documentJson, roomId, layer, x, y, width, height, cancellationToken)`. For repeated queries, create an immutable snapshot with `AutomationEngine.CreateTileQuery(documentJson, cancellationToken)` and call its `Query` method. It reuses per-layer indexes and returns fresh JSON values. Later edits do not change that snapshot.

Lua exposes this through `studio.tiles.get { roomId = "hall", layer = "foreground", x = 0, y = 0, width = 2, height = 1 }`. Queries observe the input map snapshot. Operations queued by the current script become visible only after the script commits successfully.
