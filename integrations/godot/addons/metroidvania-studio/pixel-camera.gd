@tool
extends Camera2D

@export_range(1, 8192) var pixels_per_unit: int = 16
@export var reference_resolution := Vector2i(320, 180)
@export var view_offset := Vector2.ZERO

func _process(_delta: float) -> void:
    if Engine.is_editor_hint() or not is_current(): return
    var ppu := float(maxi(1, pixels_per_unit))
    var half := Vector2(reference_resolution) * 0.5
    var desired := global_position + view_offset
    # Align the view without rounding away small movements of the camera node.
    offset = ((desired * ppu - half).round() + half) / ppu - global_position
    position_smoothing_enabled = false
    rotation_smoothing_enabled = false
    zoom = Vector2(ppu, ppu)
    force_update_scroll()
