extends SceneTree
const PixelCamera = preload("res://addons/metroidvania-studio/pixel-camera.gd")
func _initialize() -> void:
    call_deferred("run_check")
func run_check() -> void:
    for ppu in [16, 32]:
        for reference in [Vector2i(320,180), Vector2i(321,181)]:
            var camera = PixelCamera.new()
            camera.pixels_per_unit = ppu
            camera.reference_resolution = reference
            root.add_child(camera)
            camera.make_current()
            for step in range(10):
                camera.position += Vector2(0.1 / ppu, -0.1 / ppu)
                var original = camera.position
                camera._process(0)
                assert(camera.position == original)
                var edge = (camera.global_position + camera.offset) * ppu - Vector2(reference) * 0.5
                assert(edge.is_equal_approx(edge.round()))
                assert(camera.zoom == Vector2(ppu,ppu))
            assert(camera.position.is_equal_approx(Vector2(1.0 / ppu, -1.0 / ppu)))
            camera.free()
    print("Pixel camera passed: PPU, odd resolution, source-pixel alignment and accumulated motion.")
    quit()
