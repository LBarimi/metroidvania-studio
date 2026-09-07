-- Add a room property based on each room's world position.
-- Existing terrain and other properties stay intact.
local updated = 0
for _, room in ipairs(studio.rooms()) do
    if not room.locked then
        local region = room.y >= 0 and "Upper Region" or "Lower Region"
        studio.properties.set {
            roomId = room.roomId,
            values = { region = region, temporary = studio.null }
        }
        updated = updated + 1
    end
end
print("Updated region properties in " .. updated .. " rooms.")
