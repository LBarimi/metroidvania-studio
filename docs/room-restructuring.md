# Merge and split rooms

## Merge selected rooms

Hold Ctrl or Cmd and click the rooms to select them together. Choose **Edit → Merge selected rooms**, or use the same action in the inspector.

The result is one rectangular room enclosing the selection, including any space between the selected rooms. The active room keeps its ID, name and region color. Foreground tiles, background tiles, objects, triggers and path nodes keep their world positions. Placement IDs and per-object properties stay unchanged.

The merge is rejected if its rectangle would overlap an unselected room, if selected rooms already overlap, or if a selected room is locked. Different values for the same custom room property must be resolved first. Room-specific backdrop filters must also agree.

## Split a selection into a room

Choose the selection tool and drag a rectangle inside the active room. Choose **Edit → Split selection into a room**, or use the same action in the inspector.

The selected rectangle becomes a new room. The remainder is partitioned into up to four rectangular rooms without overlap or gaps. The largest remaining part keeps the original room ID and name; the other rooms get unique IDs and names. The new selected room is focused automatically.

Splitting includes every tile and object layer, including hidden layers and groups. All content stays at its original world position. Complete objects, trigger rectangles, decals and path nodes move together into the room that contains them. If a cut would cross one of these elements, adjust the selection or move the element first.

Room properties are copied to each part. Compatible backdrop filters are extended to the new rooms. Custom property values are opaque: references inside your own property strings are not rewritten automatically.

## Save and undo

Each merge or split creates one Undo step. Room JSON exports and the minimap update through the normal editing flow. Undo restores the original rooms and placement IDs; Redo restores the same generated room IDs. The exported data continues to use map format 2.
