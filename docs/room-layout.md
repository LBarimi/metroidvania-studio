# Room layout

Build a connected world by adding rooms, resizing their bounds, and arranging them on the map canvas. Move rooms individually or select several rooms to reposition a whole section.

## Add and select rooms

Right-click empty space in the map editor and choose the room creation action. The new room is placed near that position without overlapping existing rooms. Its suggested size covers the current reference resolution, rounded up to whole tiles: 320 × 180 starts at 20 × 12 tiles, and 640 × 360 at 40 × 23. You can change the dimensions before creating it. A new blank map also keeps the current camera settings and starts with a room sized to match.

Click a room to select it. While painting, the first left or right click in a different room only activates it. Start another stroke to paint or erase there. The selected room's properties appear in the inspector.

## Move and resize

Drag a room's title strip to move it. Bring room edges together to snap them into place. Drag the outer resize handles to change the room's bounds. These handles work with every tool and layer, including Game camera. The inspector also provides dimensions, rotation and reflection controls.

When shrinking a room, existing contents limit how far its bounds can move inward. Rooms touching a resized edge may move with it to preserve their layout. Rooms separated by a gap stay in place; a resize stops if it would overlap them.

Hold **Ctrl** and click rooms to build a selection, then drag a selected room's title strip to move the selection together. Click empty space to clear the multiple selection. Use **Edit → Undo** to undo a layout change.

## Navigate the world

Middle-drag to pan and use the mouse wheel to zoom. Right-drag on empty space also pans; a right click without dragging opens the room creation menu with any tool selected. Use **Fit map** or **Frame room** in the toolbar to focus the editing canvas.

The **PPU** and **Resolution** toolbar controls set the camera framing in [Game Preview](game-preview.md), the window in the canvas's upper-right corner. Select **Game camera** in Tools to drag its white frame, or turn on **Always show game camera** to see it while painting. The camera starts at the room's lower-left corner; the Center control moves it to the room's center. Imported engine cameras use the same starting corner.

## Paint connected spaces

Select a tile palette and paint the room's foreground or background layer. Autotiling updates neighboring tile edges as you draw. Use [tile palette settings](tilesets.md) to choose your source images and connection mode.

Open the [minimap](minimap.md) to review the overall layout. Room adjacency describes the map layout; gameplay transitions are implemented in your game project.

## Fit to resolution

In the room properties, choose **Fit to resolution** to size the room for the current reference resolution. Room dimensions round up to whole 16-pixel tiles: 320 × 180 becomes 20 × 12 tiles. The room keeps its position.

When contents would be removed, a warning shows the number of affected tiles and objects before applying the change. Both tile layers and all object layers are included. An object is deleted entirely if its transformed body or any path node extends outside the new bounds. Cancel keeps everything; Undo restores the room, deleted contents and any attached rooms moved by the resize.
