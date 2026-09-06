using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace MetroidvaniaStudio.Integration.Editor
{
    public sealed class StudioConnectionWindow : EditorWindow
    {
        [Serializable] public sealed class Snapshot
        {
            public string instanceId; public long revision, documentRevision, catalogRevision;
            public MapDocument document; public CatalogData catalog; public CameraProfile camera;
        }
        [SerializeField] private string serverUrl = "http://127.0.0.1:18765/";
        [SerializeField] private string selectedRoomId;
        [SerializeField] private bool autoSync;
        private Snapshot snapshot;
        private bool busy, connected, loaded;
        private double nextPoll;
        private string status = "";
        private CancellationTokenSource lifetime;
        private readonly HttpClient client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false }) { Timeout = TimeSpan.FromSeconds(10) };
        private StudioResourceLibrary library;
        private string libraryToken, loadedToken;
        private static string L(string kr, string en) => Application.systemLanguage == SystemLanguage.Korean ? kr : en;
        [MenuItem("Tools/MetroidvaniaStudio")]
        public static void Open() { GetWindow<StudioConnectionWindow>("MetroidvaniaStudio"); }
        private void OnEnable() { lifetime = new CancellationTokenSource(); EditorApplication.update += Poll; }
        private void OnDisable() { EditorApplication.update -= Poll; lifetime.Cancel(); lifetime.Dispose(); }
        private void OnDestroy() { client.Dispose(); }
        private void OnGUI()
        {
            EditorGUILayout.LabelField("MetroidvaniaStudio", EditorStyles.boldLabel);
            using (new EditorGUI.DisabledScope(busy || EditorApplication.isPlayingOrWillChangePlaymode))
            {
                serverUrl = EditorGUILayout.TextField(L("로컬 스튜디오 주소", "Local studio URL"), serverUrl);
                if (GUILayout.Button(L("연결 / 새로고침", "Connect / Refresh"))) Run(async () => { snapshot = null; libraryToken = null; connected = false; loaded = false; await Refresh(false); connected = true; });
                if (GUILayout.Button(L("JSON 파일 가져오기", "Import JSON files"))) ImportFiles();
                if (snapshot?.document?.rooms != null)
                {
                    var rooms = snapshot.document.rooms;
                    int selected = Math.Max(0, rooms.FindIndex(room => room.id == selectedRoomId));
                    int next = EditorGUILayout.Popup(L("방", "Room"), selected, rooms.ConvertAll(room => room.name).ToArray());
                    if (rooms.Count > 0) { if (selectedRoomId != rooms[next].id) loaded = false; selectedRoomId = rooms[next].id; }
                    if (GUILayout.Button(L("방 로드 / 다시 로드", "Load / Reload room"))) Run(() => Load(true));
                    if (GUILayout.Button(L("생성한 방 제거", "Remove imported room"))) { StudioSceneImport.Remove(SceneManager.GetActiveScene()); loaded = false; }
                    autoSync = EditorGUILayout.Toggle(L("웹 편집 자동 반영", "Follow web edits"), autoSync);
                    var profile = snapshot.camera; EditorGUILayout.LabelField($"PPU {profile.ppu}  |  {profile.referenceWidth} × {profile.referenceHeight}");
                }
            }
            EditorGUILayout.HelpBox(status.Length == 0 ? L("웹 스튜디오를 실행한 뒤 연결하세요. JSON 파일로도 가져올 수 있습니다.", "Start the web studio and connect, or import JSON files.") : status, MessageType.Info);
        }
        public static Uri LocalAddress(string value)
        {
            if (!Uri.TryCreate(value, UriKind.Absolute, out Uri uri) || uri.Scheme != "http" || !uri.IsLoopback || uri.UserInfo.Length != 0)
                throw new InvalidOperationException("Use an HTTP loopback address without credentials.");
            return new Uri(uri.GetLeftPart(UriPartial.Authority) + "/");
        }
        private void Poll()
        {
            if (!connected || !autoSync || !loaded || busy || EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.timeSinceStartup < nextPoll) return;
            nextPoll = EditorApplication.timeSinceStartup + 2; Run(() => Refresh(true));
        }
        private async void Run(Func<Task> work)
        {
            if (busy) return; busy = true;
            try { await work(); if (this) status = L("연결 / 가져오기 완료", "Connected / Import complete"); }
            catch (OperationCanceledException) { }
            catch (Exception error) { if (this) status = error.Message; }
            finally { if (this) { busy = false; Repaint(); } }
        }
        private async Task Refresh(bool updateRoom)
        {
            Uri root = LocalAddress(serverUrl);
            string query = snapshot == null ? "" : $"?since={snapshot.revision}&instanceId={Uri.EscapeDataString(snapshot.instanceId)}&documentRevision={snapshot.documentRevision}&catalogRevision={snapshot.catalogRevision}&full=true";
            using (var response = await client.GetAsync(new Uri(root, "api/state" + query), lifetime.Token))
            {
                if (response.StatusCode == System.Net.HttpStatusCode.NoContent) return;
                response.EnsureSuccessStatusCode();
                string json = await response.Content.ReadAsStringAsync(); lifetime.Token.ThrowIfCancellationRequested();
                if (json.Length > 64 * 1024 * 1024) throw new InvalidOperationException("Studio response is too large.");
                var next = JsonUtility.FromJson<Snapshot>(json);
                bool changed = snapshot == null || next.instanceId != snapshot.instanceId || next.documentRevision != snapshot.documentRevision || next.catalogRevision != snapshot.catalogRevision;
                if (snapshot != null && next.instanceId == snapshot.instanceId) { next.document = next.document ?? snapshot.document; next.catalog = next.catalog ?? snapshot.catalog; }
                StudioRoomBuilder.Validate(next.document);
                if (next.catalog == null || next.camera == null) throw new InvalidOperationException("Studio resource catalog or camera settings are missing.");
                snapshot = next;
                if (!snapshot.document.rooms.Exists(room => room.id == selectedRoomId))
                { if (updateRoom && loaded) StudioSceneImport.Remove(SceneManager.GetActiveScene(), false); selectedRoomId = snapshot.document.rooms.Count == 0 ? null : snapshot.document.rooms[0].id; loaded = false; }
                if (updateRoom && changed && loaded) { try { await Load(false); } catch { snapshot = null; throw; } }
            }
        }
        private async Task Load(bool recordUndo)
        {
            if (snapshot == null) return;
            Scene scene = SceneManager.GetActiveScene();
            string token = snapshot.instanceId + ":" + snapshot.catalogRevision + ":" + snapshot.camera.ppu;
            if (!library || libraryToken != token)
            {
                if (!connected && snapshot.instanceId == "file") throw new InvalidOperationException("Import the resource files again.");
                var textures = new Dictionary<string, byte[]>(StringComparer.Ordinal);
                foreach (string name in StudioResourceImport.TextureNames(snapshot.catalog))
                {
                    var bytes = await client.GetByteArrayAsync(new Uri(LocalAddress(serverUrl), "api/asset?path=" + Uri.EscapeDataString(name)));
                    lifetime.Token.ThrowIfCancellationRequested();
                    if (bytes.Length > 32 * 1024 * 1024) throw new InvalidOperationException("Texture is too large.");
                    textures[name] = bytes;
                }
                library = StudioResourceImport.Create(snapshot.catalog, snapshot.camera.ppu, textures); libraryToken = token;
            }
            lifetime.Token.ThrowIfCancellationRequested();
            var room = snapshot.document.rooms.Find(value => value.id == selectedRoomId);
            if (room == null) return;
            var roomDocument = new MapDocument { formatVersion = snapshot.document.formatVersion, tileSize = snapshot.document.tileSize, name = snapshot.document.name,
                rooms = new List<MapRoom> { room }, properties = snapshot.document.properties, layerGroups = snapshot.document.layerGroups, stylegrounds = snapshot.document.stylegrounds };
            string nextToken = StudioResourceImport.Hash(Encoding.UTF8.GetBytes(JsonUtility.ToJson(roomDocument) + JsonUtility.ToJson(snapshot.camera) + libraryToken));
            if (!recordUndo && loaded && loadedToken == nextToken) return;
            StudioSceneImport.Load(roomDocument, selectedRoomId, library, snapshot.camera, scene, recordUndo: recordUndo); loaded = true; loadedToken = nextToken;
        }
        private void ImportFiles()
        {
            string mapPath = EditorUtility.OpenFilePanel(L("맵 JSON", "Map JSON"), "", "json"); if (mapPath.Length == 0) return;
            string catalogPath = EditorUtility.OpenFilePanel(L("리소스 catalog.json", "Resource catalog.json"), Path.GetDirectoryName(mapPath), "json"); if (catalogPath.Length == 0) return;
            string resourceRoot = EditorUtility.OpenFolderPanel(L("Textures 폴더를 포함한 리소스 폴더", "Resource directory containing Textures"), Path.GetDirectoryName(catalogPath), ""); if (resourceRoot.Length == 0) return;
            Run(() =>
            {
                var document = StudioRoomBuilder.Parse(File.ReadAllText(mapPath, new UTF8Encoding(false, true)));
                var catalog = JsonUtility.FromJson<CatalogData>(File.ReadAllText(catalogPath, new UTF8Encoding(false, true)).TrimStart('\uFEFF'));
                var profile = MapCameraSettings.Resolve(document, catalog.camera);
                var textures = new Dictionary<string, byte[]>(StringComparer.Ordinal);
                foreach (string name in StudioResourceImport.TextureNames(catalog)) textures[name] = File.ReadAllBytes(StudioResourceImport.ResolveTexturePath(resourceRoot, name));
                library = StudioResourceImport.Create(catalog, profile.ppu, textures);
                snapshot = new Snapshot { instanceId = "file", document = document, catalog = catalog, camera = profile };
                libraryToken = "file:0:" + profile.ppu; selectedRoomId = document.rooms.Count > 0 ? document.rooms[0].id : null; connected = false; loaded = false;
                return Task.CompletedTask;
            });
        }
    }
}
