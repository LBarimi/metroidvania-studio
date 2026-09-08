# Game Preview

Game Preview sits in the upper-right corner of the map canvas. It shows the selected room using the map's PPU and reference resolution while you continue editing. Tile strokes appear before you release the mouse. Other rooms, the editing grid, room borders and selection handles are hidden in Preview.

- Use **−** to collapse the window and **+** to expand it.
- Use the square button to maximize Preview within the map canvas. Press it again or **Esc** to restore its size.
- Select **Game camera** in the left Tools panel to show a white camera frame and central crosshair in the active room. Drag anywhere inside that frame to move the camera; the pointer becomes a hand and Preview follows immediately.
- Turn on **Always show game camera** beside **Frame room** to keep the frame visible while painting or using other tools. This viewing preference is remembered locally; the frame does not intercept those tools.
- Drag the room's title strip to move it, or its outer handles to resize it, with any tool selected, including Game camera. Locked rooms remain protected.
- Drag inside Preview to move the same camera. Press **F** while it is focused, or use the center button, to return to the selected room's center.
- **Pixel Perfect** snaps camera movement to source-pixel steps. Uncheck it for smooth movement in both Preview and the white camera frame. It starts checked and remembers your choice in this browser.
- Change **PPU** or **Resolution** in the main toolbar. Preview updates automatically. Source tiles remain 16 pixels wide, so changing PPU changes world units rather than the number of tiles in a fixed pixel resolution.

Preview preserves the full camera aspect ratio. It uses integer magnification when space allows and nearest-neighbor reduction when the reference resolution exceeds the window size. Maximize it to inspect more detail. Panning or maximizing Preview leaves your editing camera unchanged.

Selecting another room recenters Preview. Collapsing it or switching to the minimap pauses its rendering. The collapsed setting is remembered on this browser. Preview does not run game logic or simulate a player.

Held tile strokes update Preview once per animation frame. At native or integer magnification with Pixel Perfect enabled, only changed regions and their neighboring tiles are redrawn. Reduced or smooth views reuse the full-frame rendering path to preserve pixel edges. Camera, room, texture and viewport changes refresh the whole view.

The camera stops at the room edges in either mode. If a room is narrower or shorter than the camera, that axis stays centered: the editor clips the white box to the room, while Preview keeps the full resolution and shows empty space beyond it.

Camera positioning is a local viewing aid and does not modify map JSON or Undo history. Choose another tool or press **Esc** to leave Game camera. It is available on every layer, including locked layers and rooms. Resizing a room or changing resolution clamps the camera to the updated bounds.
