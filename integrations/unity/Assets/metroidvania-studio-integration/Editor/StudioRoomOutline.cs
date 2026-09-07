using System;
using System.Runtime.CompilerServices;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace MetroidvaniaStudio.Integration.Editor
{
    public static class StudioRoomOutline
    {
        private sealed class Outline
        {
            public string json, id;
            public int ppu;
            public readonly Vector3[] corners = new Vector3[5];
            public bool valid;
        }
        private static readonly ConditionalWeakTable<StudioImportedRoom, Outline> Cache = new ConditionalWeakTable<StudioImportedRoom, Outline>();

        [DrawGizmo(GizmoType.Selected | GizmoType.NonSelected)]
        private static void Draw(StudioImportedRoom room, GizmoType flags)
        {
            if (!SceneView.currentDrawingSceneView || !room.resources) return;
            var outline = Cache.GetOrCreateValue(room);
            if (!ReferenceEquals(outline.json, room.documentJson) || outline.id != room.roomId || outline.ppu != room.resources.ppu)
            {
                outline.json = room.documentJson; outline.id = room.roomId; outline.ppu = room.resources.ppu; outline.valid = false;
                try
                {
                    var data = StudioRoomBuilder.Parse(room.documentJson).rooms.Find(value => value.id == room.roomId);
                    if (data != null && outline.ppu > 0)
                    {
                        float width = data.width * 16f / outline.ppu, height = data.height * 16f / outline.ppu;
                        outline.corners[0] = outline.corners[4] = Vector3.zero;
                        outline.corners[1] = new Vector3(width, 0, 0);
                        outline.corners[2] = new Vector3(width, height, 0);
                        outline.corners[3] = new Vector3(0, height, 0);
                        outline.valid = true;
                    }
                }
                catch (Exception) { return; }
            }
            if (!outline.valid) return;
            var matrix = Handles.matrix; var color = Handles.color; var depth = Handles.zTest;
            try
            {
                Handles.matrix = room.transform.localToWorldMatrix;
                Handles.color = Color.white; Handles.zTest = CompareFunction.Always;
                Handles.DrawAAPolyLine(2f, outline.corners);
            }
            finally { Handles.matrix = matrix; Handles.color = color; Handles.zTest = depth; }
        }
    }
}
