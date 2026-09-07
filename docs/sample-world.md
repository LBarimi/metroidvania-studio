# Try the sample world

A fresh workspace opens **Starter World**. To reopen it later, choose **Help → Open sample world**. If your current map has unsaved changes, save it first or use the confirmation dialog to discard those changes. Opening the sample creates an editable copy; the bundled original stays intact.

## A five-minute tour

1. **Paint Landing.** Select the first room, then drag the brush along the floor. Try a diagonal shape or right-drag to erase. Use Undo to restore your edit.
2. **Explore Canopy.** Select the second room. Toggle the background layer's eye icon to compare its terrain with the backdrop.
3. **Arrange the upper route.** Ctrl-click Sky Bridge and Lift Shaft. Drag a selected room's title strip to move the pair together. Undo, then try resizing a room using its outer handles or rotating it in the inspector.
4. **Visit Foundry.** Select the objects or triggers layer, then move the marker or resize the trigger rectangle. These are sample data for editing; they do not implement game behavior.
5. **Read the minimap.** Follow the loop from Landing through Canopy, Lift Shaft, Sky Bridge and Lookout. Double-click a room to return to the map editor at that room.

## Keep experimenting

Use the palette's **+** button to add a color for your own area. **File → Save map as** saves your edited world to a JSON file. Room exports continue automatically in the workspace.

To try external image editing, follow [Texture editing and engine resources](textures.md). The sample uses the standard catalog and image files. Your custom catalogs and existing saved maps take priority over the sample at startup.

The world data is also available as `samples/maps/starter-world.map.json` in the source download. No game engine or network connection is needed to explore it.
