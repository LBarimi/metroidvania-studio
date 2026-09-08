-- Run in an empty map. The passage uses a shared room boundary.
local hall = studio.room.add {
    id = "entry-hall", name = "Entry Hall",
    x = 0, y = 0, width = 24, height = 14
}
local tower = studio.room.add {
    id = "upper-tower", name = "Upper Tower",
    x = 24, y = 0, width = 16, height = 28
}

studio.tiles.rectangle {
    roomId = hall, layer = "foreground",
    x = 0, y = 0, width = 24, height = 2
}
studio.tiles.rectangle {
    roomId = tower, layer = "foreground",
    x = 0, y = 0, width = 16, height = 2
}
for row = 6, 22, 4 do
    local left = row % 8 == 6 and 3 or 9
    studio.tiles.rectangle {
        roomId = tower, layer = "foreground",
        x = left, y = row, width = 4, height = 1
    }
end
studio.camera.set { ppu = 16, width = 320, height = 180 }
print("Created a hall and a connected vertical route.")
