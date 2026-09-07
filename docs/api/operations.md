# Operation reference

All operations are objects with an `op` string. Required fields appear first below. Unknown fields, duplicate fields, incorrect types, and unsupported layer/shape names fail the batch. Optional fields keep their current value unless a default is listed.

## Rooms

| Operation | Required fields | Optional fields |
| --- | --- | --- |
| `room.add` | `x`, `y`, `width`, `height` | `id`, `name` |
| `room.update` | `roomId` | `name`, `visible` |
| `room.move` | `roomId`, `x`, `y` | — |
| `room.resize` | `roomId`, `width`, `height` | `x`, `y`, `crop: false`, `keepLocalContents: false` |
| `room.rotate` | `roomId` | `clockwise: true` |
| `room.duplicate` | `roomId`, `x`, `y` | `id`, `name` |
| `room.delete` | `roomId` | — |

`room.add`, `room.move`, and `room.duplicate` resolve an overlapping destination against existing rooms. They place the moved or created room at a free boundary and keep other rooms fixed. Inspect the returned document to obtain its final coordinates.

`room.resize` uses the same contact layout as manual editing: attached rooms can move with the changed edge, while rooms separated by a gap stay fixed. A new overlap with a detached room rejects the operation. Without `crop`, the bounds stop at existing terrain. With `crop`, out-of-bounds target-room terrain is removed. By default, existing contents keep their world position when the origin changes; `keepLocalContents: true` keeps their local coordinates instead. This matches the editor's size fields. Existing object and node positions follow that resize behavior; crop removes terrain only.

`room.rotate` turns by 90 degrees, transforms tile slopes, objects, and nodes, and uses the contact layout if width and height change. `room.duplicate` copies all supported room content and custom properties with fresh identities. `room.delete` also removes its tiles and objects.

## Tiles

Tile layers are `foreground` and `background`. Shapes are `solid`, `bottomLeft`, `bottomRight`, `topLeft`, and `topRight`; a triangular shape is named after its filled right-angle corner.

| Operation | Required fields | Optional fields |
| --- | --- | --- |
| `tiles.paint` | `roomId`, `layer`, `cells` | `materialId: "terrain"`, `groupId: ""`, `shape: "solid"` |
| `tiles.erase` | `roomId`, `layer`, `cells` | `groupId: ""` |
| `tiles.rectangle` | `roomId`, `layer`, `x`, `y`, `width`, `height` | `materialId: "terrain"`, `groupId: ""`, `shape: "solid"`, `erase: false` |
| `tiles.fill` | `roomId`, `layer`, `x`, `y` | `materialId: "terrain"`, `groupId: ""`, `shape: "solid"`, `erase: false` |

`cells` is an array of `{ "x": 0, "y": 0 }` points. A point can also provide its own `shape`, overriding the operation's shape. Repeated coordinates are allowed; the last write wins. All coordinates and full rectangles must fit inside the room. Erasing a missing cell is a no-op.

```json
{
  "apiVersion": 1,
  "operations": [
    {
      "op": "tiles.paint",
      "roomId": "east",
      "layer": "foreground",
      "materialId": "terrain",
      "cells": [
        { "x": 2, "y": 2, "shape": "bottomRight" },
        { "x": 3, "y": 2 },
        { "x": 4, "y": 2 }
      ]
    }
  ]
}
```

`tiles.fill` visits four-connected cells matching the starting cell's material, shape, and group. Empty cells match other empty cells. A different material, shape, or group forms a barrier. Group locks apply both to the destination group and to any existing cell that would be replaced or erased.

## Objects

Object layers are `entities`, `triggers`, `foregroundDecals`, and `backgroundDecals`.

| Operation | Required fields | Optional fields |
| --- | --- | --- |
| `object.add` | `roomId`, `definitionId`, `layer`, `x`, `y` | `id`, `groupId: ""`, `width: 1`, `height: 1`, `rotation: 0`, `scaleX: 1`, `scaleY: 1`, `nodes`, `properties` |
| `object.update` | `roomId`, `objectId` | `definitionId`, `groupId`, `x`, `y`, `width`, `height`, `rotation`, `scaleX`, `scaleY`, `nodes`, `properties` |
| `object.delete` | `roomId`, `objectId` | — |

Dimensions must be positive, scales must be nonzero, and all numeric values must be finite. Added or updated objects, including their rotated/scaled bounds and nodes, must fit within their room. `nodes` replaces the node array and contains `{ "x": 0, "y": 0 }` points. Updating position alone does not offset an explicitly stored node path; provide the intended nodes when moving that path.

`properties` merges string values by key; a `null` value removes a key. Layer changes are not supported by `object.update`; recreate the object in the required layer. Definition-specific rendering and placement restrictions belong to the host's catalog validation.

## Properties and camera

| Operation | Required fields | Optional fields |
| --- | --- | --- |
| `properties.set` | `values` | `roomId` |
| `document.update` | `name` | — |
| `camera.set` | `ppu`, `width`, `height` | — |

`properties.set` targets the document when `roomId` is absent. `values` is an object of string or null values. Existing unrelated keys remain unchanged.

```json
{
  "apiVersion": 1,
  "operations": [
    {
      "op": "properties.set",
      "roomId": "east",
      "values": { "region": "forest", "music": "ambient", "obsolete": null }
    },
    { "op": "camera.set", "ppu": 16, "width": 320, "height": 180 }
  ]
}
```

Camera PPU must be an integer from 1 to 8,192. Reference width and height must be integers from 1 to 16,384. These settings are persisted through the existing camera property keys and do not resize source tile textures.
