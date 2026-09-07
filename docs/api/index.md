# Editing API

The editing API is shared by Lua, the command line, MCP, and the web studio. It works on map JSON without an engine, graphics window, filesystem, or network connection.

Each request contains an API version and an ordered batch. Room and object operations use explicit IDs. A successful batch returns one complete document; a failed or canceled batch returns no edited document. The web studio commits a successful batch as one undoable edit.

```json
{
  "apiVersion": 1,
  "operations": [
    { "op": "room.add", "id": "east", "name": "East Hall", "x": 40, "y": 0, "width": 24, "height": 12 },
    { "op": "tiles.rectangle", "roomId": "east", "layer": "foreground", "materialId": "terrain", "x": 0, "y": 0, "width": 24, "height": 2 },
    { "op": "camera.set", "ppu": 16, "width": 320, "height": 180 }
  ]
}
```

Use [bounded tile queries](queries.md) to inspect existing terrain without transferring a full map. Use [the operation reference](operations.md) for fields and behavior. [The JSON Schema](automation.schema.json) describes the complete request envelope. `AutomationEngine.Describe()` returns the same schema with operation descriptions, coordinates, capabilities, and limits.

## Calling from .NET

Reference `metroidvania-studio/automation/MetroidvaniaStudio.Automation.csproj`.

```csharp
using System.Text.Json;
using MetroidvaniaStudio.Automation;

using var request = JsonDocument.Parse(requestJson);
AutomationResult result = AutomationEngine.Apply(mapJson, request.RootElement, cancellationToken);
string updatedMap = result.DocumentJson;
JsonElement inspection = AutomationEngine.Inspect(updatedMap, cancellationToken);
JsonElement capabilities = AutomationEngine.Describe();
```

`AutomationResult` contains:

| Field | Meaning |
| --- | --- |
| `DocumentJson` | Validated map JSON in format version 2. |
| `CreatedIds` | Created room and object IDs, in creation order. |
| `OperationCount` | Number of operations processed, including valid no-ops. |
| `Changed` | Whether the normalized document changed. |

`Apply` never mutates the caller's string or stores a global document. Callers decide when to save or commit the result. Concurrent editors must compare the original document revision before committing; a pure batch does not resolve competing writes by itself.

## Coordinates and resources

Room `x` and `y` are world tile coordinates. Tiles use integer coordinates relative to their room's bottom-left corner. Positive Y points upward. Object coordinates and dimensions are local tile units and can contain fractions. Source tiles remain 16 by 16 pixels; camera PPU and reference resolution are separate document settings.

Use resource catalog IDs for `materialId` and `definitionId`. The API stores these IDs and never downloads or opens resource files. It does not create textures or infer engine asset paths. A host or engine adapter resolves the IDs against its catalog.

## Validation and compatibility

API version `1` and map format version `2` are separate contracts. Existing format version 1 maps use the core's migration to version 2. Custom data belongs in the document, room, object, or styleground `properties` lists. Unrelated properties, stylegrounds, groups, and supported resource IDs are retained. Unknown JSON fields are rejected rather than silently discarded, matching the map loader.

IDs are case-sensitive and globally unique across rooms, objects, groups, and stylegrounds. Explicit IDs make generated content repeatable. Omitted creation IDs receive generated values. Room duplication derives copied object IDs from the explicit new room ID and source object IDs, so repeating an identical batch against identical input produces identical copies.

Locked rooms reject edits. Locked groups include their locked ancestors; painting, erasing, object changes, room deletion, resizing, and rotation cannot modify their contents. Room movement preserves local contents and can move an unlocked room containing locked groups. Automation does not expose an operation to unlock content.

Malformed requests throw `ArgumentException`, with the zero-based operation index for operation failures. Invalid document data throws `InvalidDataException`. Cancellation throws `OperationCanceledException`. No partial result is returned for these failures.

## Limits

| Limit | Maximum |
| --- | --- |
| Request JSON | 8 MiB UTF-8 |
| Input or output map JSON | 32 MiB UTF-8 |
| Operations per batch | 1,024 |
| Visited tile cells per batch | 1,048,576 |
| Work units per batch | 8,388,608 |
| Rooms per document | 1,024 |
| Room width or height | 1,024 tiles |
| Objects per room | 16,384 |
| Nodes per object | 4,096 |

Work includes indexed tile changes, hierarchy and object lookups, property merging, and the document snapshots required by room transformations. Large repeated room transformations can hit the work limit before the operation limit. Split such work into smaller batches and inspect each result.

Tile writes share a cached dictionary for each room/layer throughout the batch, avoiding a scan of all existing tiles for every new cell. Cancellation is checked before and after document processing and during bounded edit loops. Parsing, core validation, and an individual core room transformation are synchronous; hosts that need a strict deadline should run work outside the UI thread and enforce their own timeout.

## Inspection

`Inspect` returns `apiVersion`, `formatVersion`, `tileSize`, `name`, camera settings, document properties, and room summaries. A room summary contains `roomId`, name, bounds, visibility, lock state, foreground/background counts, properties, and object summaries with `objectId` and `definitionId`.

The `minimap` object contains world tile bounds and shared room-edge connections. Each connection has `roomAId`, `roomBId`, `vertical`, `coordinate`, `start`, and `end`. `truncated` reports whether the connection limit was reached. These are layout connections; the renderer determines the visible openings and outline styling. Inspection does not change selection or move the editor camera.
