# Map JSON format

The single source of the document contract is `Core/Workspace/MapDocument.cs` and its referenced data classes. `Contracts/Program.cs` reflects compiled types to generate `Web/contracts.generated.ts`. The same build generates `Contracts/map-format-v2.schema.json`; `node MetroidvaniaStudio/build-web.mjs --check-contracts` checks both outputs for drift.

## Version and encoding

Documents use `formatVersion: 2` and `tileSize: 16`. Output is UTF-8 without a byte-order mark; an optional UTF-8 byte-order mark is accepted on input. Version 1 files migrate to version 2 with empty layer groups. Future versions, unknown structural fields, duplicate JSON keys and invalid values are rejected before replacing the current document. A missing optional field retains its documented model default.

The JSON schema describes structural validation. `MapDocumentStore` additionally validates unique IDs, group references, room containment, finite coordinates and bounded aggregate work. Each map file and an aggregate room export are limited to 32 MiB. A room can be up to 1024 by 1024 tiles; a document can contain at most 1024 rooms. The software release version is independent of the map format version.

## Coordinates and identity

Room positions are integer world coordinates in tile units. Positive X points right and positive Y points up. Tile cells, object positions and path nodes use coordinates local to their room. Object rectangles use a lower-left origin; rotation is in degrees around the rectangle center. A cell is 16 by 16 source pixels. Preview pixel scale and reference resolution are catalog settings and do not change the stored map coordinates.

Shape values remain stable: 0 is solid, 1 fills the bottom-left triangle, 2 the bottom-right triangle, 3 the top-left triangle and 4 the top-right triangle. Layer values are 0 foreground tiles, 1 background tiles, 2 entities, 3 triggers, 4 foreground decorations and 5 background decorations. The value 6 selects all layers in editing commands and is not a stored content layer.

Room and object IDs are stable opaque strings. A tile material or object definition ID is resolved through the selected workspace catalog, never through a machine path or engine object. Existing identifiers retain their spelling. Sprite atlas entries contain a relative texture resource name and a source rectangle. Applications importing JSON provide their own resource resolver; map documents do not embed images or execute object definitions.

## Import and export

A complete authoring file contains rooms, shared properties, background layers and layer groups. Each exported room file uses the same document envelope with one room, preserving relevant shared metadata and stable IDs. This lets a consumer use one reader for complete maps and individual rooms.

User-defined object properties and unknown background effect properties are opaque data and survive an import/export round trip. The editor does not run their game behavior. Unknown structural fields require an explicit format update instead of being silently discarded.

Any future incompatible change requires a format-version increment and an explicit migrator. Generated declarations and JSON schema, literal version-2 compatibility tests, Unicode round trips and existing format migration tests must pass together before publication.
