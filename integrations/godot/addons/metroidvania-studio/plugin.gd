@tool
extends EditorPlugin
const Loader = preload("room-loader.gd")
var panel: VBoxContainer
var picker: EditorFileDialog
var map_path := ""
var catalog_path := ""
var resources := ""
var rooms: OptionButton
var status: Label
var room_ids: Array[String] = []

func _enter_tree() -> void:
    panel = VBoxContainer.new()
    panel.name = "MetroidvaniaStudio"
    for entry in [["1. Map JSON", 0], ["2. Catalog JSON", 1], ["3. Resource folder", 2]]:
        var button := Button.new()
        button.text = entry[0]
        var kind: int = entry[1]
        button.pressed.connect(func(): choose(kind))
        panel.add_child(button)
    rooms = OptionButton.new()
    panel.add_child(rooms)
    var load_button := Button.new()
    load_button.text = "Import / Reload room"
    load_button.pressed.connect(import_room)
    panel.add_child(load_button)
    status = Label.new()
    status.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART
    status.custom_minimum_size.x = 210
    status.text = "Select a map, catalog and texture directory."
    panel.add_child(status)
    add_control_to_dock(DOCK_SLOT_RIGHT_UL, panel)

func _exit_tree() -> void:
    if is_instance_valid(picker): picker.queue_free()
    remove_control_from_docks(panel)
    panel.queue_free()

func choose(kind: int) -> void:
    if is_instance_valid(picker): picker.queue_free()
    picker = EditorFileDialog.new()
    picker.access = EditorFileDialog.ACCESS_FILESYSTEM
    picker.file_mode = EditorFileDialog.FILE_MODE_OPEN_DIR if kind == 2 else EditorFileDialog.FILE_MODE_OPEN_FILE
    if kind != 2: picker.add_filter("*.json", "JSON")
    get_editor_interface().get_base_control().add_child(picker)
    if kind == 2:
        picker.dir_selected.connect(func(value): resources = value; status.text = "Resource folder selected.")
    else:
        picker.file_selected.connect(func(value):
            if kind == 0:
                map_path = value
                var loader = Loader.new()
                var document: Dictionary = loader.read_json(value)
                rooms.clear()
                room_ids.clear()
                for room in document.get("rooms", []):
                    rooms.add_item(str(room.get("name", room.get("id", ""))))
                    room_ids.append(str(room.get("id", "")))
                status.text = loader.error if not loader.error.is_empty() else "Map selected."
            else:
                catalog_path = value
                status.text = "Catalog selected."
        )
    picker.popup_centered_ratio(0.65)

func import_room() -> void:
    if map_path.is_empty() or catalog_path.is_empty() or resources.is_empty() or rooms.selected < 0:
        status.text = "Select all three inputs and a room."
        return
    var loader = Loader.new()
    var root: Node2D = loader.load_room(map_path, catalog_path, resources, room_ids[rooms.selected])
    if root == null:
        status.text = loader.error
        return
    var folder := "res://metroidvania-studio-imports"
    DirAccess.make_dir_recursive_absolute(folder)
    # Stable opaque ID hash prevents room names from becoming filesystem paths.
    var output := folder.path_join(room_ids[rooms.selected].sha256_text().substr(0,24) + ".tscn")
    var scene := PackedScene.new()
    var result := scene.pack(root)
    if result == OK: result = ResourceSaver.save(scene, output)
    root.free()
    if result != OK:
        status.text = "Could not save the imported scene."
        return
    get_editor_interface().get_resource_filesystem().scan()
    if output in get_editor_interface().get_open_scenes():
        get_editor_interface().reload_scene_from_path(output)
    else:
        get_editor_interface().open_scene_from_path(output)
    status.text = "Room saved. Textures and collision are embedded."
