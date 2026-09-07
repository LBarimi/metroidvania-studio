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
            Check(camera.orthographicSize == 3.5f && camera.transform.position.x == -1 && camera.transform.position.y == 1.75f, "Camera centers on the scaled room");
            var renderTexture = new RenderTexture(1000, 600, 16); camera.targetTexture = renderTexture; camera.GetComponent<StudioPixelCamera>().Apply();
            Check(Mathf.Approximately(camera.rect.width, .8f) && Mathf.Approximately(camera.rect.height, 448f / 600), "Camera viewport uses integer pixel magnification");
            Check(Mathf.Approximately(camera.pixelRect.x, 100) && Mathf.Approximately(camera.pixelRect.y, 76), "Camera viewport is centered on whole pixels");
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
