# Lua API reference

Scripts use a restricted Lua 5.2 interpreter and `studio.api_version == 1`. The automation API version is separate from the application's version and the map JSON format version.

## Inspect the input map

- `studio.document()` returns document metadata, camera settings, room summaries, properties, and minimap connections.
- `studio.rooms()` returns the room summaries as a Lua sequence starting at index 1.
- `studio.log(...)` and `print(...)` collect messages shown in the result.
- `studio.tiles.get { roomId, layer, x, y, width, height }` reads a rectangular tile window from the input snapshot. The window must lie inside the room and cover at most 4,096 cells. The returned `cells` sequence contains only occupied cells with `x`, `y`, `shape`, `materialId`, and `groupId`. Missing coordinates are empty.

Room summaries include `roomId`, `name`, `x`, `y`, `width`, `height`, `visible`, `locked`, `foregroundCount`, `backgroundCount`, `objectCount`, `properties`, and `objects`. Object summaries include `objectId`, `definitionId`, `layer`, `x`, `y`, `width`, and `height`.

These are detached snapshots of the map **before** the script started. Changing a returned table does not edit the map. Queued operations do not appear in later snapshot queries. Keep returned IDs and calculated positions in local variables while constructing a batch.

## Edit the map

Each method accepts exactly one table. The table fields match the common automation API; the method name supplies `op` automatically.

| Lua method | Required fields | Common optional fields |
| --- | --- | --- |
| `studio.room.add` | `x, y, width, height` | `id, name` |
| `studio.room.update` | `roomId` | `name, visible` |
| `studio.room.move` | `roomId, x, y` | |
| `studio.room.resize` | `roomId, width, height` | `x, y, crop, keepLocalContents` |
| `studio.room.rotate` | `roomId` | `clockwise` |
| `studio.room.duplicate` | `roomId, x, y` | `id, name` |
| `studio.room.delete` | `roomId` | |
| `studio.tiles.paint` | `roomId, layer, cells` | `materialId, groupId` |
| `studio.tiles.erase` | `roomId, layer, cells` | `groupId` |
| `studio.tiles.rectangle` | `roomId, layer, x, y, width, height` | `materialId, shape, erase, groupId` |
| `studio.tiles.fill` | `roomId, layer, x, y` | `materialId, shape, erase, groupId` |
| `studio.properties.set` | `values` | `roomId` |
| `studio.camera.set` | `ppu, width, height` | |
| `studio.object.add` | `roomId, definitionId, layer, x, y` | `id, width, height, groupId, properties` |
| `studio.object.update` | `roomId, objectId` | `x, y, width, height, rotation, scaleX, scaleY, definitionId, properties` |
| `studio.object.delete` | `roomId, objectId` | |
| `studio.rename` | `name` | |

`studio.room.add`, `studio.room.duplicate`, and `studio.object.add` return the created ID. Pass explicit IDs when another script or external system needs stable names. Otherwise the runner assigns IDs without colliding with existing rooms or objects.

`studio.apply { op = "room.add", ... }` accepts the common operation form directly. It has the same limits and validation as named methods. Use `studio.rename { name = "World" }` for the `document.update` operation.

Tile layers are `"foreground"` and `"background"`. Object layers are `"entities"`, `"triggers"`, `"foregroundDecals"`, and `"backgroundDecals"`. A cell is `{ x = 0, y = 0, shape = "solid" }`; shape can also be `"bottomLeft"`, `"bottomRight"`, `"topLeft"`, or `"topRight"`. Paint defaults to material `"terrain"` and shape `"solid"`.

Array fields use consecutive integer keys starting at 1. Room/world coordinates and tile cells are integers. Objects support fractional local coordinates. Each resource uses a catalog ID such as `materialId` or `definitionId`, not a filesystem path.

Use strings for property values. To remove a property, pass `studio.null`; assigning Lua `nil` removes the table entry before the operation reaches the API.

```lua
studio.properties.set {
    roomId = "entry",
    values = { region = "Garden", temporary = studio.null }
}
```

Resize preserves content by default. `crop = true` explicitly permits cropping. Overlapping rooms, invalid IDs, out-of-bounds painting, and edits to protected content fail the whole batch.

## Available Lua functions

Lua control flow, local functions, tables, arithmetic, concatenation, and comparisons are available. Basic functions include `assert`, `error`, `ipairs`, `pairs`, `next`, `select`, `tonumber`, `tostring`, and `type`.

- `math` supports numeric helpers and seeded `random`. `randomseed` is unavailable; choose the seed when running the script.
- `bit32` supplies bounded numeric bit operations.
- `string.len`, `sub`, `rep`, `lower`, `upper`, `reverse`, and `find` are available. `find` searches literal text; patterns are unavailable. Use function syntax such as `string.sub(value, 1, 3)`.
- `table.insert`, `remove`, and `concat` operate on sequences.

String helpers use the interpreter's UTF-16 text representation. Use ASCII when indexing by individual character positions. Unicode names and property values are preserved when passed as whole strings.

File I/O, networking, processes, modules, dynamic code loading, host object access, metatables, debug functions, coroutine APIs, and custom error-catching functions are unavailable. See [execution limits](execution-limits.md) before processing large maps.
