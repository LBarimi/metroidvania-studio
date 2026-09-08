@tool
class_name MetroidvaniaStudioTriggerManager
extends Node

signal trigger_requested(info: Dictionary)

const Events = preload("trigger-event.gd")
# Authored data persists with an imported scene; consumed events are session state.
@export var rooms: Array[Dictionary] = []
var error: String = ""
var _entries: Dictionary = {}
var _owners: Dictionary = {}
var _consumed: Dictionary = {}
var _initialized: bool = false

func _ready() -> void:
    _ensure_initialized()

func _ensure_initialized() -> void:
    if _initialized: return
    _initialized = true
    var saved := rooms.duplicate(true)
    rooms.clear()
    for room in saved:
        if not register_room(room): return

func register_room(room: Dictionary) -> bool:
    _ensure_initialized()
    var room_id := str(room.get("id", ""))
    if room_id.strip_edges().is_empty() or not room.get("objects", []) is Array:
        error = "Invalid trigger room."
        return false
    var next_owners := {}
    var next_entries := {}
    for object in room.get("objects", []):
        if not object is Dictionary:
            error = "Invalid placed object."
            return false
        var id := str(object.get("id", ""))
        if id.strip_edges().is_empty() or next_owners.has(id) or (_owners.has(id) and _owners[id] != room_id):
            error = "Placed object IDs must be nonempty and unique across registered rooms."
            return false
        next_owners[id] = room_id
        var definition := str(object.get("definition", "")).to_lower()
        if definition in ["portal", "invisiblewall"] or int(object.get("layer", 2)) != 3: continue
        var values := {}
        if not object.get("properties", []) is Array:
            error = "Invalid object properties."
            return false
        for property in object.get("properties", []):
            if not property is Dictionary:
                error = "Invalid object property."
                return false
            var key := str(property.get("key", ""))
            if not values.has(key): values[key] = str(property.get("value", ""))
        next_entries[id] = {
            "roomId": room_id, "roomName": str(room.get("name", "")),
            "roomX": int(room.get("x", 0)), "roomY": int(room.get("y", 0)),
            "objectId": id, "definition": str(object.get("definition", "")),
            "event": Events.parse(str(values.get("event", ""))),
            "once": str(values.get("once", "")).strip_edges().to_lower() == "true",
            "description": str(values.get("desc", ""))
        }
    unregister_room(room_id)
    rooms.append(room.duplicate(true))
    _owners.merge(next_owners)
    _entries.merge(next_entries)
    error = ""
    return true

func unregister_room(room_id: String) -> void:
    _ensure_initialized()
    for id in _owners.keys():
        if _owners[id] == room_id:
            _owners.erase(id)
            _entries.erase(id)
    for index in range(rooms.size() - 1, -1, -1):
        if str(rooms[index].get("id", "")) == room_id: rooms.remove_at(index)

func try_get(id: String) -> Dictionary:
    _ensure_initialized()
    return _entries.get(id, {}).duplicate(true)

func request_trigger(id: String) -> bool:
    var info := try_get(id)
    if info.is_empty() or info.event == Events.Id.None: return false
    if info.once:
        if _consumed.has(id): return false
        _consumed[id] = true
    trigger_requested.emit(info)
    return true

func reset_once(id: String) -> bool:
    return _consumed.erase(id)

func reset_all_once() -> void:
    _consumed.clear()

func clear() -> void:
    rooms.clear()
    _entries.clear()
    _owners.clear()
    _consumed.clear()
    _initialized = true
