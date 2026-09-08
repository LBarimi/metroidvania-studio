extends SceneTree
const Loader = preload("res://addons/metroidvania-studio/room-loader.gd")
func _initialize() -> void:
    var loader = Loader.new()
    var room: Node2D = loader.load_room("res://samples/maps/Sample.map.json", "res://samples/catalog.json", "res://samples")
    if room == null:
        push_error(loader.error)
        quit(1)
        return
    assert(room.get_meta("ppu") == 16)
    assert(room.get_node("Foreground").surfaces.size() > 0)
    assert(room.get_node("TerrainCollision").get_child_count() > 0)
    var count := 0
    for mask in range(256):
        if loader.normalize_mask(mask) == mask: count += 1
    assert(count == 47)
    var scene := PackedScene.new()
    assert(scene.pack(room) == OK)
    assert(ResourceSaver.save(scene,"res://smoke-room.tscn") == OK)
    room.free()
    var saved: Node = load("res://smoke-room.tscn").instantiate()
    assert(saved.get_node("Foreground").surfaces[0].texture != null)
    saved.free()
    assert(loader.load_room("res://samples/maps/Sample.map.json", "res://samples/catalog.json", "res://samples", "absent") == null)
    var coverage: Node2D = loader.load_room("res://samples/maps/Coverage.json", "res://samples/catalog.json", "res://samples")
    assert(coverage != null)
    assert(coverage.get_meta("ppu") == 32)
    assert(coverage.get_meta("reference_resolution") == Vector2i(640,360))
    assert(coverage.position == Vector2(-3.5,-1.5))
    assert(coverage.get_node("Foreground").surfaces.size() == 5)
    assert(coverage.get_node("TerrainCollision").get_child_count() == 5)
    var triangles := 0
    for item in coverage.get_node("Foreground").surfaces:
        if item.points.size() == 3: triangles += 1
    assert(triangles == 4)
    coverage.free()
    var regions: Node2D = loader.load_room("res://samples/maps/TriggerEvents.json", "res://samples/catalog.json", "res://samples")
    assert(regions != null)
    var region_count := 0
    for item in regions.get_children():
        if not item.has_meta("object"): continue
        var metadata: Dictionary = item.get_meta("object")
        if metadata.id == "portal":
            assert(item is Area2D and item.get_child_count() == 1 and item.get_child(0) is CollisionPolygon2D)
            region_count += 1
        elif metadata.id == "wall":
            assert(item is StaticBody2D and item.get_child_count() == 1 and item.get_child(0) is CollisionPolygon2D)
            assert(item.position == Vector2(9, -2.5))
            region_count += 1
    assert(region_count == 2)
    assert(not regions.get_node("TriggerManager").request_trigger("portal"))
    regions.free()
    print("Godot import passed: terrain, collision, atlas textures, 47 masks, scene round-trip, missing room.")
    quit(0)
