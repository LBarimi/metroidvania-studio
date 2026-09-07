# Lua quick start

Lua scripts can create rooms, paint terrain, place objects, and adjust map properties in one operation. The editor, command line, and MCP tools use the same editing API.

Open **Edit > Scripts**, paste a script, and choose **Preview**. Review the result, then run the script to apply it. A successful script creates one undo step. Cancellation, script errors, invalid edits, or a map changed during execution leave the original map unchanged.

```lua
local rooms = studio.rooms()
if #rooms == 0 then
    print("Create a room first.")
else
    local room = rooms[1]
    studio.tiles.rectangle {
        roomId = room.roomId,
        layer = "foreground",
        x = 0, y = 0,
        width = room.width, height = 1
    }
    print("Painted a floor in " .. room.name)
end
```

Coordinates are measured in tiles. Room positions are world coordinates; tile and object positions are local to their room. Positive Y points up. The tile sprite size is 16 pixels; camera PPU and resolution are separate settings.

Studio functions accept a table with named fields. A room creation returns its ID, which can be used in later operations in the same script:

```lua
local room = studio.room.add {
    name = "Hall",
    x = 0, y = 0,
    width = 24, height = 12
}
studio.tiles.rectangle {
    roomId = room, layer = "foreground",
    x = 0, y = 0, width = 24, height = 2
}
```

Start this example in an empty map, or choose coordinates clear of existing rooms. The API rejects overlapping rooms.

Use the same seed to repeat a random layout. `math.random()` and `math.random(minimum, maximum)` use the run's seed. Generated IDs are also repeatable for the same input map and script. Existing maps are not cleared automatically.

Examples:

- [Connected rooms](../examples/connected-rooms.lua)
- [Seeded platforms](../examples/seeded-platforms.lua)
- [Region properties](../examples/region-properties.lua)

For command-line execution, see the [CLI guide](../cli/quick-start.md). For exact operations, read the [Lua API reference](api-reference.md) and [execution limits](execution-limits.md).
