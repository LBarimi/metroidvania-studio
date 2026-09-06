@tool
extends Node2D
## Runtime geometry is saved into the scene; no external file access is needed.
@export var surfaces: Array[Dictionary] = []

func _draw() -> void:
    for surface in surfaces:
        if surface.get("trigger", false):
            continue
        draw_polygon(surface.points, PackedColorArray([surface.get("color", Color.WHITE)]), surface.uv, surface.get("texture"))
