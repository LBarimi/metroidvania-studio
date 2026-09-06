using System;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace MetroidvaniaStudio.Integration.Editor
{
    public static class StudioSceneImport
    {
        public static StudioImportedRoom Load(MapDocument document, string roomId, StudioResourceLibrary resources, CameraProfile profile, Scene scene, Camera camera = null, bool recordUndo = true)
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode || !scene.IsValid() || !scene.isLoaded) throw new InvalidOperationException("Load rooms into an open scene outside Play Mode.");
            GameObject prepared = StudioRoomBuilder.Build(document, roomId, resources);
            int group = -1;
            if (recordUndo) { Undo.IncrementCurrentGroup(); group = Undo.GetCurrentGroup(); Undo.SetCurrentGroupName("Load studio room"); }
            try
            {
                SceneManager.MoveGameObjectToScene(prepared, scene);
                if (recordUndo) Undo.RegisterCreatedObjectUndo(prepared, "Load studio room");
                foreach (var old in Resources.FindObjectsOfTypeAll<StudioImportedRoom>())
                    if (old && old.gameObject != prepared && old.gameObject.scene == scene) Destroy(old.gameObject, recordUndo);
                if (!camera) foreach (var root in scene.GetRootGameObjects()) foreach (var candidate in root.GetComponentsInChildren<Camera>())
                    if (candidate.CompareTag("MainCamera") && !candidate.GetComponentInParent<StudioImportedRoom>()) { camera = candidate; break; }
                if (!camera)
                {
                    var cameraObject = new GameObject("Studio Camera"); cameraObject.transform.SetParent(prepared.transform, false); cameraObject.tag = "MainCamera";
                    camera = cameraObject.AddComponent<Camera>(); camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = Color.black;
                }
                if (recordUndo) { Undo.RecordObject(camera, "Configure studio camera"); Undo.RecordObject(camera.transform, "Center studio camera"); }
                // Only one component may own this camera's projection.
                var previousPixelCamera = camera.GetComponent("PixelPerfectCamera") as Behaviour;
                if (previousPixelCamera) { if (recordUndo) Undo.RecordObject(previousPixelCamera, "Configure studio camera"); previousPixelCamera.enabled = false; }
                var pixel = camera.GetComponent<StudioPixelCamera>();
                if (!pixel) pixel = recordUndo ? Undo.AddComponent<StudioPixelCamera>(camera.gameObject) : camera.gameObject.AddComponent<StudioPixelCamera>();
                if (recordUndo) Undo.RecordObject(pixel, "Configure studio camera");
                pixel.Configure(profile);
                var room = document.rooms.Find(value => value.id == roomId); float unit = 16f / profile.ppu;
                camera.transform.position = new Vector3((room.x + room.width * .5f) * unit, (room.y + room.height * .5f) * unit, camera.transform.position.z == 0 ? -10 : camera.transform.position.z);
                camera.transform.rotation = Quaternion.identity;
                Physics2D.SyncTransforms();
                foreach (var collider in prepared.GetComponentsInChildren<CompositeCollider2D>()) collider.GenerateGeometry();
                EditorSceneManager.MarkSceneDirty(scene); EditorApplication.QueuePlayerLoopUpdate(); SceneView.RepaintAll();
                return prepared.GetComponent<StudioImportedRoom>();
            }
            finally { if (group >= 0) Undo.CollapseUndoOperations(group); }
        }
        public static void Remove(Scene scene, bool recordUndo = true)
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) return;
            foreach (var marker in Resources.FindObjectsOfTypeAll<StudioImportedRoom>())
                if (marker && marker.gameObject.scene == scene) Destroy(marker.gameObject, recordUndo);
        }
        private static void Destroy(GameObject obj, bool undo) { if (undo) Undo.DestroyObjectImmediate(obj); else UnityEngine.Object.DestroyImmediate(obj); }
    }
}
