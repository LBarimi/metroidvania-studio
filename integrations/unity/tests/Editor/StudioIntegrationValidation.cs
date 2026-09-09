using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.Tilemaps;
using MetroidvaniaStudio.Integration;
using MetroidvaniaStudio.Integration.Editor;

public static class StudioIntegrationValidation
{
    private static int passed;
    private static void Check(bool value, string name) { if (!value) throw new Exception(name); passed++; Debug.Log("PASS " + name); }
    public static void Run()
    {
        int errors = 0;
        Application.logMessageReceived += (_, __, type) => { if (type == LogType.Error || type == LogType.Exception || type == LogType.Assert) errors++; };
        try
        {
            Check(Application.unityVersion == "6000.3.9f1", "Target editor version");
            var catalog = JsonUtility.FromJson<CatalogData>(File.ReadAllText("Assets/ValidationFixtures/catalog.json"));
            var textures = new Dictionary<string, byte[]>();
            foreach (string name in StudioResourceImport.TextureNames(catalog)) textures[name] = File.ReadAllBytes(Path.Combine("Assets/ValidationFixtures", name));
            var document = new MapDocument(); var room = new MapRoom { id = "fixture", name = "Fixture", x = -4, y = 2, width = 4, height = 3 }; document.rooms.Add(room);
            string material = catalog.materials[0].id;
            for (int y = 0; y < 3; y++) for (int x = 0; x < 4; x++) room.foreground.Add(new MapCell { x = x, y = y, material = material });
            room.objects.Add(new MapObject { id = "trigger", definition = "area", layer = MapLayer.Triggers, x = 1, y = 1 });
            room.objects[0].properties.Add(new MapProperty { key = "event", value = "Trigger200" });
            room.objects[0].properties.Add(new MapProperty { key = "once", value = "true" });
            room.objects[0].properties.Add(new MapProperty { key = "desc", value = "Open gate" });
            var portal = new MapObject { id = "portal", definition = "Portal", layer = MapLayer.Entities, x = 0, y = 1, width = 1, height = 2 };
            portal.properties.Add(new MapProperty { key = "event", value = "Trigger001" }); room.objects.Add(portal);
            room.objects.Add(new MapObject { id = "wall", definition = "InvisibleWall", layer = MapLayer.Entities, x = 3, y = 0, width = 1, height = 3 });
            var defaults = MapCameraSettings.Resolve(document); Check(defaults.ppu == 16 && defaults.referenceWidth == 320 && defaults.referenceHeight == 180 && defaults.orthographicSize == 5.625f, "Default camera profile");
            MapCameraSettings.Apply(document, 32, 400, 224); var profile = MapCameraSettings.Resolve(document);
            Check(profile.orthographicSize == 3.5f, "PPU and reference resolution determine projection");
            var parsed = StudioRoomBuilder.Parse(JsonUtility.ToJson(document)); Check(MapCameraSettings.Resolve(parsed).ppu == 32, "Camera settings survive JSON round trip");
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var library = StudioResourceImport.Create(catalog, profile.ppu, textures);
            Check(library.tiles.Count > 0 && library.tiles.All(entry => entry.tile.sprite.pixelsPerUnit == 32), "Atlas sprites use selected PPU");
            Check(StudioResourceImport.Create(catalog, profile.ppu, textures) == library, "Unchanged resource imports reuse persistent assets");
            parsed.rooms[0].foreground.Find(cell => cell.x == 3 && cell.y == 2).shape = TileShape.BottomLeft;
            var camera = new GameObject("Camera").AddComponent<Camera>(); camera.tag = "MainCamera"; camera.transform.position = new Vector3(0, 0, -10);
            var root = StudioSceneImport.Load(parsed, room.id, library, profile, scene, camera, false);
            Check(root.transform.position == new Vector3(-2, 1, 0), "Negative room origin converts from tiles to world units");
            var map = root.GetComponentsInChildren<Tilemap>().Single(value => value.name == "Foreground");
            Check(map.GetUsedTilesCount() > 0 && map.GetComponentInParent<Grid>().cellSize.x == .5f, "Tilemap cell size follows PPU");
            Check(map.GetTile<Tile>(Vector3Int.zero).name.EndsWith(":255"), "Room boundary autotiling has no outside outline");
            var slopePoints = root.GetComponentInChildren<PolygonCollider2D>().GetPath(0);
            Check(slopePoints.Length == 3 && slopePoints.Contains(new Vector2(1.5f, 1)) && slopePoints.Contains(new Vector2(2, 1)) && slopePoints.Contains(new Vector2(1.5f, 1.5f)), "Slope collision follows tile geometry and PPU");
            var composite = root.GetComponentInChildren<CompositeCollider2D>(); Check(composite.pathCount > 0 && composite.geometryType == CompositeCollider2D.GeometryType.Outlines, "Terrain collision is a merged outline");
            Check(root.GetComponentInChildren<StudioPlacedObject>().data.id == "trigger" && root.GetComponentInChildren<BoxCollider2D>().isTrigger, "Object metadata and trigger regions import");
            var portalObject = root.GetComponentsInChildren<StudioPlacedObject>().Single(value => value.IsPortal);
            var wallObject = root.GetComponentsInChildren<StudioPlacedObject>().Single(value => value.IsInvisibleWall);
            Check(portalObject.GetComponent<BoxCollider2D>().isTrigger && portalObject.GetComponentInChildren<SpriteRenderer>() == null,
                "Portal imports as an independent trigger collider without automatic event delivery");
            Check(!wallObject.GetComponent<BoxCollider2D>().isTrigger && wallObject.GetComponent<BoxCollider2D>().size == new Vector2(.5f, 1.5f)
                && wallObject.GetComponentInChildren<SpriteRenderer>() == null, "Invisible wall imports solid PPU-scaled collision without a renderer");
            Check(!portalObject.TryRequestTrigger(out _) && !wallObject.TryRequestTrigger(out _), "Standalone objects reject generic event requests");
            var triggers = root.Triggers;
            int delivered = 0;
            triggers.TriggerRequested += info => { delivered++; Check(info.ObjectId == "trigger" && info.RoomX == -4 && info.RoomY == 2 && info.Event == StudioTriggerEvent.Trigger200 && info.Description == "Open gate", "Trigger request payload"); };
            Check(root.GetComponentInChildren<StudioPlacedObject>().TryRequestTrigger(out var triggerInfo), "Explicit placed-object request");
            Check(!root.GetComponentInChildren<StudioPlacedObject>().TryRequestTrigger(out _) && delivered == 1, "Once prevents repeat delivery");
            triggers.RegisterRoom(parsed.rooms[0]); Check(!triggers.TryRequest("trigger", out _), "Room reload retains consumed events");
            Check(triggers.ResetOnce("trigger") && triggers.TryRequest("trigger", out _), "Explicit reset reactivates event");
            Check(camera.orthographicSize == 3.5f && camera.transform.position.x == 4.25f && camera.transform.position.y == 4.5f, "Camera view starts at the scaled room origin");
            var renderTexture = new RenderTexture(1000, 600, 16); camera.targetTexture = renderTexture; camera.GetComponent<StudioPixelCamera>().Apply();
            Check(Mathf.Approximately(camera.rect.width, .8f) && Mathf.Approximately(camera.rect.height, 448f / 600), "Camera viewport uses integer pixel magnification");
            Check(Mathf.Approximately(camera.pixelRect.x, 100) && Mathf.Approximately(camera.pixelRect.y, 76), "Camera viewport is centered on whole pixels");
            var lower = camera.ViewportToWorldPoint(new Vector3(0, 0, 10));
            var upper = camera.ViewportToWorldPoint(new Vector3(1, 1, 10));
            Check(Vector3.Distance(lower, root.transform.position) < .0001f, "Viewport lower-left matches the room origin");
            Check(Mathf.Approximately(upper.x - lower.x, 400f / 32) && Mathf.Approximately(upper.y - lower.y, 224f / 32), "Unity viewport has exactly the exported source-pixel dimensions");
            foreach (int ppu in new[] { 6, 12, 16, 32, 128 })
            {
                var comparison = new CameraProfile { ppu = ppu, referenceWidth = 320, referenceHeight = 180 };
                camera.GetComponent<StudioPixelCamera>().Configure(comparison);
                lower = camera.ViewportToWorldPoint(new Vector3(0, 0, 10));
                upper = camera.ViewportToWorldPoint(new Vector3(1, 1, 10));
                Check(Mathf.Abs((upper.x - lower.x) * ppu / 16 - 20) < .0001f && Mathf.Abs((upper.y - lower.y) * ppu / 16 - 11.25f) < .0001f,
                    "Unity matches the web camera tile footprint at PPU " + ppu);
            }
            camera.GetComponent<StudioPixelCamera>().Configure(profile);
            camera.targetTexture = null; UnityEngine.Object.DestroyImmediate(renderTexture);
            var pixel = camera.GetComponent<StudioPixelCamera>(); pixel.referenceWidth = 401; pixel.referenceHeight = 225;
            pixel.SendMessage("LateUpdate");
            Check(Mathf.Approximately(camera.transform.position.x * 32 - 200.5f, Mathf.Round(camera.transform.position.x * 32 - 200.5f)), "Odd resolution camera aligns source-pixel edges");
            pixel.Configure(profile);
            var second = StudioSceneImport.Load(parsed, room.id, library, profile, scene, camera, true);
            Check(UnityEngine.Object.FindObjectsByType<StudioImportedRoom>(FindObjectsSortMode.None).Length == 1 && second, "Reload replaces only the imported room");
            Undo.PerformUndo(); Check(UnityEngine.Object.FindObjectsByType<StudioImportedRoom>(FindObjectsSortMode.None).Length == 1, "Reload supports Undo");
            EditorSceneManager.SaveScene(scene, "Assets/ValidationScene.unity");
            scene = EditorSceneManager.OpenScene("Assets/ValidationScene.unity", OpenSceneMode.Single);
            var restored = UnityEngine.Object.FindFirstObjectByType<StudioImportedRoom>();
            Check(restored && restored.resources && restored.GetComponentsInChildren<Tilemap>().Any(value => value.GetTile(Vector3Int.zero)), "Scene reload preserves imported sprite and tile asset references");
            bool rejected = false; try { StudioConnectionWindow.LocalAddress("http://example.invalid/"); } catch (InvalidOperationException) { rejected = true; }
            Check(rejected, "Studio connection rejects external hosts");
            rejected = false; try { StudioResourceImport.ResolveTexturePath("Assets/ValidationFixtures", "../outside.png"); } catch (InvalidOperationException) { rejected = true; }
            Check(rejected, "Offline resource resolver rejects directory traversal");
            parsed.formatVersion = 999; rejected = false; try { StudioRoomBuilder.Build(parsed, room.id, library); } catch (InvalidOperationException) { rejected = true; }
            Check(rejected && UnityEngine.Object.FindFirstObjectByType<StudioImportedRoom>(), "Invalid document leaves existing scene intact");
            StudioSceneImport.Remove(scene, false); Check(!UnityEngine.Object.FindFirstObjectByType<StudioImportedRoom>(), "Remove cleans imported room");
            if (errors > 0) throw new Exception("Integration produced engine errors: " + errors);
            File.WriteAllText("validation-result.json", "{\"passed\":" + passed + ",\"failed\":0}");
            Debug.Log("Studio integration validation passed: " + passed); EditorApplication.Exit(0);
        }
        catch (Exception error) { Debug.LogException(error); File.WriteAllText("validation-result.json", "{\"passed\":" + passed + ",\"failed\":1}"); EditorApplication.Exit(1); }
    }
}
