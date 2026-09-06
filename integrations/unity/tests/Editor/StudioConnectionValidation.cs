using System;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using MetroidvaniaStudio.Integration;
using MetroidvaniaStudio.Integration.Editor;

public static class StudioConnectionValidation
{
    private static void Check(bool value, string text) { if (!value) throw new Exception(text); Debug.Log("PASS " + text); }
    public static async void Run()
    {
        StudioConnectionWindow window = null;
        try
        {
            string url = Environment.GetEnvironmentVariable("METROIDVANIA_STUDIO_TEST_URL");
            var address = StudioConnectionWindow.LocalAddress(url);
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            window = ScriptableObject.CreateInstance<StudioConnectionWindow>(); // Windowless fixture; never Show/GetWindow.
            var type = typeof(StudioConnectionWindow); const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            type.GetField("serverUrl", flags).SetValue(window, address.ToString());
            Task Refresh(bool update) => (Task)type.GetMethod("Refresh", flags).Invoke(window, new object[] { update });
            Task Load() => (Task)type.GetMethod("Load", flags).Invoke(window, new object[] { false });
            await Refresh(false); type.GetField("connected", flags).SetValue(window, true); await Load();
            var first = UnityEngine.Object.FindFirstObjectByType<StudioImportedRoom>();
            Check(first && first.resources && first.resources.tiles.Count > 0, "Local connection downloads JSON and texture resources");
            string selectedId = first.roomId;
            using (var client = new HttpClient(new HttpClientHandler { UseProxy = false }))
            {
                async Task Command(string fields)
                {
                    var state = JsonUtility.FromJson<StudioConnectionWindow.Snapshot>(await client.GetStringAsync(new Uri(address, "api/state")));
                    string json = "{" + fields + ",\"clientId\":\"connection-test\",\"commandId\":\"" + Guid.NewGuid().ToString("N") + "\",\"expectedInstanceId\":\"" + state.instanceId + "\",\"expectedRevision\":" + state.revision + "}";
                    using (var response = await client.PostAsync(new Uri(address, "api/command"), new StringContent(json, Encoding.UTF8, "application/json"))) response.EnsureSuccessStatusCode();
                }
                await Command("\"action\":\"cameraSettings\",\"ppu\":32,\"referenceWidth\":400,\"referenceHeight\":224"); await Refresh(true);
                var updated = UnityEngine.Object.FindFirstObjectByType<StudioImportedRoom>();
                Check(updated && updated.resources.ppu == 32 && UnityEngine.Object.FindFirstObjectByType<StudioPixelCamera>().referenceWidth == 400, "Web camera edits refresh the loaded room and camera");
                int identity = updated.GetInstanceID();
                await Command("\"action\":\"roomAdd\",\"name\":\"Adjacent fixture\",\"x\":100,\"y\":0,\"width\":4,\"height\":4"); await Refresh(true);
                Check(UnityEngine.Object.FindFirstObjectByType<StudioImportedRoom>().GetInstanceID() == identity, "Editing a different room does not recreate the loaded scene");
                await Command("\"action\":\"roomDelete\",\"id\":\"" + selectedId + "\""); await Refresh(true);
                Check(!UnityEngine.Object.FindFirstObjectByType<StudioImportedRoom>(), "Deleting the loaded room removes its generated scene objects");
            }
            UnityEngine.Object.DestroyImmediate(window); window = null;
            Debug.Log("Studio connection validation passed: 4"); EditorApplication.Exit(0);
        }
        catch (Exception error) { if (window) UnityEngine.Object.DestroyImmediate(window); Debug.LogException(error); EditorApplication.Exit(1); }
    }
}
