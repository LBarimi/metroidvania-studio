-- Paint platforms in the first unlocked room. Choose a seed when running.
local target = nil
for _, room in ipairs(studio.rooms()) do
    if not room.locked then
        target = room
        break
    end
end
assert(target, "Create an unlocked room first.")
assert(target.width >= 12 and target.height >= 10, "Use a room at least 12 by 10 tiles.")

studio.tiles.rectangle {
    roomId = target.roomId, layer = "foreground",
    x = 0, y = 0, width = target.width, height = 2
}
local count = 0
for y = 5, target.height - 3, 4 do
    local width = math.random(3, 6)
    local x = math.random(1, target.width - width - 1)
    studio.tiles.rectangle {
        roomId = target.roomId, layer = "foreground",
        x = x, y = y, width = width, height = 1
    }
    count = count + 1
end
print("Added " .. count .. " platforms to " .. target.name)
