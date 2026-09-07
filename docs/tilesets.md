# Tile palettes

Open **…** beside a palette in the left sidebar. The same settings apply to its solid and slope brushes. Palette changes affect every tile using that palette; use **+** to create an independent palette first when needed.

## Four source tiles

Choose **4-tile autotiling**, then import a PNG strip or four separate PNGs. Each source tile is **16×16 pixels**. Select a destination slot, click a source tile to preview it, then press **Assign selected tile**. Clicking alone never changes a rule.

| Slot | Source orientation |
| --- | --- |
| 1 | Top edge, with terrain continuing below |
| 2 | Top-right outer corner, with terrain continuing left and below |
| 3 | Top-left inner corner, with an opening toward the top left |
| 4 | Interior fill |

A 64×16 strip fills these four slots from left to right. Multiple PNGs fill slots in the file selection order. Importing one 16×16 PNG replaces the selected slot. The source-image dropdown lets you assign tiles from different files.

Ordinary edges and corners retain the complete source tile, rotated for each direction. More complex connections combine 8×8 quarters to cover all 47 normalized neighbor patterns. Check thin walls, isolated tiles and corners in **Connection preview**: the four source drawings need compatible quarter boundaries.

Open **Slope tiles** to assign the four diagonal directions separately. Slopes are not inferred from the solid tiles. Unassigned slopes use the default shape.

## Forty-seven source tiles

Choose **47-tile assignment** for direct control over every connection. Each slot shows its expected shape until assigned. **Load template** provides a sheet ordered from the top left, row by row: 47 solid tiles followed by four slopes. **Download PNG** saves the selected source image. **Assign in order** assigns cells from the selected sheet; existing assignments beyond its cells remain unchanged.

Missing slots use the palette's default color and white edges. Switching the method clears the slot assignments inside the dialog; **Cancel** keeps the saved palette unchanged. **Apply** saves the palette and updates its tiles immediately.

## Original images and engine resources

Imports copy source PNGs into `Textures/palettes/` beside the launcher or at the source project root. **Storage folders** opens that location or copies its path. In a browser, select the folder once with **Set file dialog folder** to remember it for PNG open/save dialogs; the Windows application sets it automatically. The **Source image** field displays that workspace resource path after saving and reopening the dialog. Edit this copy to receive live updates; the initially selected external file is not watched.

The studio builds a generated atlas when you apply settings or save an original image. Incomplete image writes retain the previous atlas until a complete replacement is available. Composition runs on image changes, not on paint gestures.

The catalog keeps authoring settings in `editorTileset` and exposes the resulting sprites through its existing `sprites` entries. Map JSON and palette IDs remain unchanged. Engine adapters continue reading the generated PNG and catalog; no engine-specific rules are embedded in the map. Keep the catalog and workspace textures together when sharing resources. See [Texture editing and engine resources](textures.md).

**Default tile view** in the map toolbar temporarily displays the plain colored tiles. It changes only the viewport, not exported data or palette assignments.

Source images must total at most 8 MiB and 4,194,304 pixels per palette. PNG color types, transparency, standard row filters and interlaced images are supported. Every assigned region must fit a complete 16×16 tile.
