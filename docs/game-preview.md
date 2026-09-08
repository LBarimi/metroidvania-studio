# Game Preview

Game Preview sits in the upper-right corner of the map canvas. It shows the selected room using the map's PPU and reference resolution while you continue editing. Tile strokes appear before you release the mouse. Other rooms, the editing grid, room borders and selection handles are hidden in Preview.

- Use **−** to collapse the window and **+** to expand it.
- Use the square button to maximize Preview within the map canvas. Press it again or **Esc** to restore its size.
- Drag inside Preview to move its camera. Press **F** while it is focused, or use the center button, to return to the selected room's center.
- Change **PPU** or **Resolution** in the main toolbar. Preview updates automatically. Source tiles remain 16 pixels wide, so changing PPU changes world units rather than the number of tiles in a fixed pixel resolution.

Preview preserves the full camera aspect ratio. It uses integer magnification when space allows and nearest-neighbor reduction when the reference resolution exceeds the window size. Maximize it to inspect more detail. Panning or maximizing Preview leaves your editing camera unchanged.

Selecting another room recenters Preview. Collapsing it or switching to the minimap pauses its rendering. The collapsed setting is remembered on this browser. Preview does not run game logic or simulate a player.
