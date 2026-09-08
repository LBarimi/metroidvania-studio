extends SceneTree
const Manager = preload("res://addons/metroidvania-studio/trigger-manager.gd")

func _initialize() -> void:
    var document: Dictionary = JSON.parse_string(FileAccess.get_file_as_string("res://samples/maps/TriggerEvents.json"))
    var room: Dictionary = document.rooms[0]
    var manager := Manager.new()
    assert(manager.register_room(room))
    manager.trigger_requested.connect(func(info: Dictionary):
        if info.objectId == "once": assert(not manager.request_trigger("once"))
    )
    assert(manager.try_get("once").once)
    assert(manager.request_trigger("once"))
    assert(not manager.request_trigger("once"))
    var info := manager.try_get("once")
    assert(info.roomX == -7 and info.roomY == 3 and info.event == 200 and info.description == "Open gate")
    assert(manager.request_trigger("repeat") and manager.request_trigger("repeat"))
    assert(manager.request_trigger("portal") and not manager.request_trigger("portal"))
    assert(not manager.request_trigger("legacy") and not manager.request_trigger("absent"))
    room.x = 10
    assert(manager.register_room(room) and not manager.request_trigger("once"))
    assert(manager.try_get("once").roomX == 10)
    manager.unregister_room(room.id)
    assert(manager.register_room(room) and not manager.request_trigger("once"))
    var other := room.duplicate(true)
    other.id = "other"
    assert(not manager.register_room(other))
    assert(manager.try_get("once").roomId == room.id)
    assert(manager.reset_once("once") and manager.request_trigger("once"))
    manager.reset_all_once()
    assert(manager.request_trigger("portal"))
    for i in range(1, 201): assert(Manager.Events.parse("Trigger%03d" % i) == i)
    for value in ["old", "Trigger201", "Trigger1", "-1", "+1", "1.0", "999999999999999999"]:
        assert(Manager.Events.parse(value) == 0)
    var root := Node2D.new()
    root.add_child(manager)
    manager.owner = root
    manager.name = "TriggerManager"
    var scene := PackedScene.new()
    assert(scene.pack(root) == OK)
    root.free()
    var restored := scene.instantiate()
    assert(restored.get_node("TriggerManager").request_trigger("once"))
    restored.free()
    print("Godot trigger requests passed: payload, events, once, portal, reset, reentry, duplicate rejection and scene persistence.")
    quit(0)
