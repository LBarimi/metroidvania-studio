# Minimap design

Review your world as colored rooms with white outlines and entrance marks. The minimap follows the room layout and lets you jump directly to a room for editing.

## Navigate and edit

Switch to the minimap using the view tabs. Click a room to select it, or double-click it to return to the map editor with that room focused.

Middle-drag to pan and use the mouse wheel to zoom. The toolbar provides whole-world framing and selected-room framing. Enable room names when you need labels while inspecting a large world.

## Room colors

Select a room and change its region color in the inspector using the color swatch or HEX field. The minimap uses that color for the room's interior.

Unselected room interiors appear dimmer. White outlines and entrance marks stay clear for both selected and unselected rooms.

## Outlines and entrances

The minimap toolbar has separate **Outline** and **Entrance length** controls. Their defaults are **9** and **15**. Both scale with zoom so their proportions remain consistent as you zoom in or out.

Entrance marks follow the room connections calculated from the layout. To change which rooms meet, return to the map editor and adjust their positions or boundaries. See [Room layout](room-layout.md).

## Try a complete world

Open **Help → Open sample world** and follow its connected rooms in the minimap. Double-click one room, edit its layout, then return to the minimap to inspect the result. The [sample walkthrough](sample-world.md) covers the full sequence.
