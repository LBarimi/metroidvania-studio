@tool
extends RefCounted
const RoomView = preload("room-view.gd")
const OFFSETS = [Vector2i(0, 1), Vector2i(1, 1), Vector2i(1, 0), Vector2i(1, -1), Vector2i(0, -1), Vector2i(-1, -1), Vector2i(-1, 0), Vector2i(-1, 1)]
var error: String = ""

func read_json(path: String) -> Dictionary:
    error = ""
    var file := FileAccess.open(path, FileAccess.READ)
    if file == null or file.get_length() > 32 * 1024 * 1024:
        error = "Cannot read JSON, or the file exceeds 32 MiB."
        return {}
    var parser := JSON.new()
    if parser.parse(file.get_as_text().trim_prefix("\ufeff")) != OK or not parser.data is Dictionary:
        error = "Invalid JSON document."
        return {}
    return parser.data

static func normalize_mask(mask: int) -> int:
    if mask & 5 != 5: mask &= ~2
    if mask & 20 != 20: mask &= ~8
    if mask & 80 != 80: mask &= ~32
    if mask & 65 != 65: mask &= ~128
    return mask & 255

static func has_edge(shape: int, offset: Vector2i) -> bool:
    match shape:
        0: return true
        1: return offset.x < 0 or offset.y < 0
        2: return offset.x > 0 or offset.y < 0
        3: return offset.x < 0 or offset.y > 0
        4: return offset.x > 0 or offset.y > 0
    return false

static func polygon(shape: int) -> PackedVector2Array:
    match shape:
        0: return PackedVector2Array([Vector2(0,0), Vector2(1,0), Vector2(1,1), Vector2(0,1)])
        1: return PackedVector2Array([Vector2(0,0), Vector2(1,0), Vector2(0,1)])
        2: return PackedVector2Array([Vector2(0,0), Vector2(1,0), Vector2(1,1)])
        3: return PackedVector2Array([Vector2(0,0), Vector2(1,1), Vector2(0,1)])
        4: return PackedVector2Array([Vector2(1,0), Vector2(1,1), Vector2(0,1)])
    return PackedVector2Array()

func visible(group_id: String, groups: Dictionary) -> bool:
    var seen := {}
    while not group_id.is_empty():
        if seen.has(group_id) or not groups.has(group_id):
            error = "Missing or cyclic layer group."
            return false
        seen[group_id] = true
        var group: Dictionary = groups[group_id]
        if not group.get("visible", true): return false
        group_id = group.get("parentId", "")
    return true

func surface(sprite: Dictionary, resource_root: String, cache: Dictionary, points: PackedVector2Array, unit_uv: PackedVector2Array) -> Dictionary:
    var asset: String = sprite.get("asset", "")
    if asset.is_empty() or asset.is_absolute_path() or asset.contains(":") or asset.contains("\\") or asset.split("/").has("..") or asset.split("/").has("."):
        error = "Resource paths must be relative and remain inside the resource directory."
        return {}
    var directory := DirAccess.open(resource_root)
    if directory == null:
        error = "Cannot open the resource directory."
        return {}
    var accumulated := ""
    for part in asset.split("/"):
        accumulated = accumulated.path_join(part)
        if directory.is_link(accumulated):
            error = "Resource symlinks are not supported."
            return {}
    if not cache.has(asset):
        var image := Image.load_from_file(resource_root.path_join(asset))
        if image == null or image.is_empty() or image.get_width() > 16384 or image.get_height() > 16384:
            error = "Missing or unsupported texture: " + asset
            return {}
        cache[asset] = ImageTexture.create_from_image(image)
    var texture: Texture2D = cache[asset]
    var x: int = sprite.get("x", 0)
    var y: int = sprite.get("y", 0)
    var w: int = sprite.get("width", 16)
    var h: int = sprite.get("height", 16)
    if x < 0 or y < 0 or w < 1 or h < 1 or x + w > texture.get_width() or y + h > texture.get_height():
        error = "Sprite rectangle is outside the texture."
        return {}
    var uv := PackedVector2Array()
    for point in unit_uv:
        uv.append(Vector2((x + point.x * w) / texture.get_width(), 1.0 - (y + point.y * h) / texture.get_height()))
    return {"points": points, "uv": uv, "texture": texture}

func fail(root: Node, message: String) -> Node2D:
    error = message
    root.free()
    return null

func load_room(map_path: String, catalog_path: String, resource_root: String, room_id: String = "") -> Node2D:
    var document := read_json(map_path)
    if document.is_empty(): return null
    var catalog := read_json(catalog_path)
    if catalog.is_empty(): return null
    if int(document.get("formatVersion", 0)) not in [1, 2] or document.get("tileSize", 0) != 16:
        error = "Unsupported map format or tile size."
        return null
    var rooms = document.get("rooms", [])
    if not rooms is Array or rooms.is_empty() or rooms.size() > 1024:
        error = "Invalid room list."
        return null
    var room: Dictionary = {}
    var ids := {}
    for item in rooms:
        if not item is Dictionary or str(item.get("id", "")).is_empty() or ids.has(item.id):
            error = "Invalid or duplicate room ID."
            return null
        ids[item.id] = true
        if item.id == room_id or (room_id.is_empty() and room.is_empty()): room = item
    if room.is_empty():
        error = "Room ID was not found."
        return null
    var width: int = room.get("width", 0)
    var height: int = room.get("height", 0)
    if width < 1 or height < 1 or width > 1024 or height > 1024:
        error = "Room dimensions must be between 1 and 1024."
        return null
    var camera: Dictionary = catalog.get("camera", {}) if catalog.get("camera") is Dictionary else {}
    var ppu: int = camera.get("ppu", 16)
    var reference := Vector2i(camera.get("referenceWidth", 320), camera.get("referenceHeight", 180))
    for property in document.get("properties", []):
        var key: String = property.get("key", "")
        if key.begins_with("metroidvaniaStudio.camera."):
            var value := str(property.get("value", ""))
            if not value.is_valid_int():
                error = "Invalid camera property."
                return null
            match key:
                "metroidvaniaStudio.camera.ppu": ppu = value.to_int()
                "metroidvaniaStudio.camera.width": reference.x = value.to_int()
                "metroidvaniaStudio.camera.height": reference.y = value.to_int()
    if ppu < 1 or ppu > 8192 or reference.x < 1 or reference.y < 1 or reference.x > 16384 or reference.y > 16384:
        error = "Invalid camera profile."
        return null
    var unit := 16.0 / ppu
    var root := Node2D.new()
    root.name = "MetroidvaniaStudioRoom"
    root.position = Vector2(room.get("x", 0), -room.get("y", 0)) * unit
    root.set_meta("room", room)
    root.set_meta("document", document)
    root.set_meta("ppu", ppu)
    root.set_meta("reference_resolution", reference)
    var groups := {}
    for group in document.get("layerGroups", []):
        if groups.has(group.get("id", "")): return fail(root, "Duplicate layer group.")
        groups[group.get("id", "")] = group
    var materials := {}
    var definitions := {}
    for material in catalog.get("materials", []): materials[material.id] = material
    for definition in catalog.get("objects", []): definitions[str(definition.id).to_lower()] = definition
    var cache := {}
    var body := StaticBody2D.new()
    body.name = "TerrainCollision"
    root.add_child(body)
    body.owner = root
    for layer in ["background", "foreground"]:
        var view = RoomView.new()
        view.name = layer.capitalize()
        view.texture_filter = CanvasItem.TEXTURE_FILTER_NEAREST
        view.z_index = -20 if layer == "background" else 0
        root.add_child(view)
        view.owner = root
        var cells := {}
        for cell in room.get(layer, []):
            var pos := Vector2i(cell.get("x", 0), cell.get("y", 0))
            var shape: int = cell.get("shape", 0)
            if pos.x < 0 or pos.y < 0 or pos.x >= width or pos.y >= height or shape < 0 or shape > 4 or cells.has(pos):
                return fail(root, "Invalid or duplicate tile cell.")
            cells[pos] = cell
        for pos in cells:
            var cell: Dictionary = cells[pos]
            if not visible(cell.get("groupId", ""), groups):
                if not error.is_empty(): return fail(root, error)
                continue
            var shape: int = cell.get("shape", 0)
            var material: String = cell.get("material", "terrain")
            var mask := 0
            for i in range(8):
                var offset: Vector2i = OFFSETS[i]
                var neighbor_pos: Vector2i = pos + offset
                var outside: bool = neighbor_pos.x < 0 or neighbor_pos.y < 0 or neighbor_pos.x >= width or neighbor_pos.y >= height
                var neighbor: Dictionary = cells.get(neighbor_pos, {})
                var connected: bool = outside or (not neighbor.is_empty() and neighbor.get("material", "terrain") == material and visible(neighbor.get("groupId", ""), groups))
                if connected and has_edge(shape, offset) and has_edge(0 if outside else neighbor.get("shape", 0), -offset): mask |= 1 << i
            if not error.is_empty(): return fail(root, error)
            mask = normalize_mask(mask) if shape == 0 else 0
            if not materials.has(material): return fail(root, "Missing material: " + material)
            var sprite: Dictionary = {}
            for candidate in materials[material].get("sprites", []):
                if candidate.get("shape", 0) == shape:
                    if candidate.get("mask", 0) == mask:
                        sprite = candidate
                        break
                    if candidate.get("mask", 0) == 0: sprite = candidate
            if sprite.is_empty() or sprite.get("width",16) != 16 or sprite.get("height",16) != 16: return fail(root, "Missing 16 by 16 terrain sprite.")
            var points := PackedVector2Array()
            var unit_uv := polygon(shape)
            for point in unit_uv: points.append(Vector2(pos.x + point.x, -pos.y - point.y) * unit)
            var data := surface(sprite, resource_root, cache, points, unit_uv)
            if data.is_empty(): return fail(root, error)
            view.surfaces.append(data)
            if layer == "foreground":
                var collision := CollisionPolygon2D.new()
                collision.polygon = points
                body.add_child(collision)
                collision.owner = root
    var object_ids := {}
    for object in room.get("objects", []):
        if object_ids.has(object.get("id", "")): return fail(root, "Duplicate object ID.")
        object_ids[object.get("id", "")] = true
        if not visible(object.get("groupId", ""), groups):
            if not error.is_empty(): return fail(root, error)
            continue
        var layer: int = object.get("layer", 2)
        var w: float = object.get("width", 1)
        var h: float = object.get("height", 1)
        if not layer in [2,3,4,5] or w <= 0 or h <= 0: return fail(root, "Invalid object dimensions or layer.")
        var holder: Node2D = Area2D.new() if layer == 3 else Node2D.new()
        holder.name = "Object"
        holder.set_meta("object", object)
        holder.position = Vector2(object.get("x",0) + w / 2, -object.get("y",0) - h / 2) * unit
        holder.rotation_degrees = -float(object.get("rotation",0))
        holder.scale = Vector2(object.get("scaleX",1),object.get("scaleY",1))
        root.add_child(holder)
        holder.owner = root
        var points := PackedVector2Array()
        var unit_uv := polygon(0)
        for point in unit_uv: points.append(Vector2((point.x - 0.5)*w, -(point.y-0.5)*h)*unit)
        if layer == 3:
            var collision := CollisionPolygon2D.new()
            collision.polygon = points
            holder.add_child(collision)
            collision.owner = root
        else:
            var definition: Dictionary = definitions.get(str(object.get("definition", "object")).to_lower(), {})
            var data: Dictionary
            if definition.get("sprite") is Dictionary:
                data = surface(definition.sprite, resource_root, cache, points, unit_uv)
                if data.is_empty(): return fail(root, error)
            else: data = {"points":points, "uv":PackedVector2Array(), "color":Color.from_string(definition.get("color", "#FFFFFF"), Color.WHITE)}
            var view = RoomView.new()
            view.texture_filter = CanvasItem.TEXTURE_FILTER_NEAREST
            view.z_index = -10 if layer == 5 else 20 if layer == 4 else 10
            view.surfaces.append(data)
            holder.add_child(view)
            view.owner = root
    var view_camera := Camera2D.new()
    view_camera.name = "RoomCamera"
    view_camera.position = Vector2(width, -height) * unit * 0.5
    view_camera.zoom = Vector2(ppu, ppu)
    root.add_child(view_camera)
    view_camera.owner = root
    return root
