# Unity integration

Installation guides: [한국어](../../engine-packages/unity/INSTALL_KR.txt) · [English](../../engine-packages/unity/INSTALL_EN.txt) · [日本語](../../engine-packages/unity/INSTALL_JP.txt) · [简体中文](../../engine-packages/unity/INSTALL_CN.txt) · [繁體中文](../../engine-packages/unity/INSTALL_TW.txt).

The five UTF-8 guides are included in the `.unitypackage` and install under `Assets/MetroidvaniaStudioIntegration`.

Developed for **Unity 6**. The web studio and its server remain independent of this adapter.

## Install and connect

1. Use `engine-packages/unity/metroidvania-studio.unitypackage`, included in the repository and standalone builds. To regenerate it from source, run the platform build script (`platform/win/web/build.bat` on Windows); `Build-UnityPackage.bat` invokes the same build.
2. With your Unity project open, double-click the package and select **Import**. It installs under `Assets/MetroidvaniaStudioIntegration` with stable GUIDs.
3. Start the web studio with the platform run script (`platform/win/web/run.bat` on Windows).
4. In Unity, open **Tools > Metroidvania Studio**, connect to the displayed loopback address, select a room and press **Load / Reload room**.
5. Enable **Follow web edits** to refresh the loaded room while this window is open. Polling runs every two seconds, pauses in Play Mode and skips unchanged server revisions. Changes in another room do not rebuild the loaded room. Disconnecting does not delete imported data.

The window follows the document currently open in the studio. It never sends map mutations to the studio. Changing room selection requires Load; removing or reloading targets only the adapter's generated room objects in the active scene. Explicit loads support Undo. Automatic refresh does not add continuous Undo entries. Remove also supports Undo.

## Offline JSON import

Use **Import JSON files** to select a map/room JSON, its `catalog.json`, and the resource directory containing the relative texture paths. Then select and load a room. JSON alone does not embed textures. The bundled `samples/catalog.json` and `samples/textures` can load maps authored with the default resources.

Imported textures, sprites and tile resources are saved under `Assets/MetroidvaniaStudioImports`. They survive scene reload and work without the studio running. The resource library is reused when its catalog, texture bytes and PPU are unchanged. Existing libraries remain available for saved scenes and Undo; the importer does not delete older imports automatically.

## Coordinates and camera

- Source tiles remain 16×16 pixels. A tile spans `16 / PPU` world units.
- Room, cell, object and trigger coordinates use the same conversion.
- Camera size is `referenceHeight / (2 × PPU)`. Defaults are PPU 16 and 320×180.
- `StudioPixelCamera` owns orthographic size and an integer-scaled viewport. Screens smaller than the reference resolution use integer downsampling.
- Load places the bottom-left of the scene's main camera view at the room's bottom-left corner, or creates a camera if none exists. An existing pixel-camera controller on that camera is disabled to avoid competing projection settings.

Terrain uses persistent Tilemap resources, the shared eight-neighbor mask, boundary continuation, slope sprites, and a merged CompositeCollider2D outline. Objects carry `StudioPlacedObject` metadata; sprite definitions are displayed and trigger rectangles become trigger colliders. Game behavior and custom background effects remain the consuming project's responsibility. The selected room and shared authored metadata are retained on `StudioImportedRoom`.

Runtime APIs are in `MetroidvaniaStudio.Integration`. Import tools are in the editor-only `MetroidvaniaStudio.Integration.Editor` assembly. Runtime code does not reference editor assemblies. DTOs, camera metadata logic and mask geometry are generated from the studio's shared code; regenerate them with `metroidvania-studio/build-web.mjs`.

## Validation

`tests/Run-Validation.ps1` builds the package, imports it into a fresh disposable project, and runs background integration checks with Unity 6000.3.9f1. Pass `-UnityPath` if the Editor is installed outside its standard directory. Logs and results stay under the ignored `.local/validation` folder. It does not open or alter an existing game project. Pass `-Connection` to additionally verify texture download and automatic room updates against a disposable local server workspace.

## 한국어

저장소에 포함된 `engine-packages/unity/metroidvania-studio.unitypackage`를 사용하세요. Unity 프로젝트를 열고 패키지를 더블클릭한 뒤 **Import**를 누르면 됩니다. 소스에서 다시 생성하려면 루트 `platform` 폴더에서 운영체제에 맞는 **build** 파일을 실행하세요.

웹 스튜디오를 실행한 상태에서 Unity의 **Tools > Metroidvania Studio**를 열고 **연결 / 새로고침 → 방 선택 → 방 로드** 순서로 사용합니다. **웹 편집 자동 반영**을 켜면 창이 열린 동안 변경 내용을 반영합니다. PPU와 기준 해상도는 웹의 **카메라 설정**에서 바꿉니다.

JSON 파일만 가져올 때는 맵 JSON과 함께 리소스 카탈로그 및 텍스처 폴더를 선택해야 합니다. 가져온 리소스는 Unity 프로젝트에 저장되므로 이후에는 웹 스튜디오를 끄더라도 씬을 사용할 수 있습니다.

Imported room boundaries are drawn in white in Scene view when Gizmos are enabled. They are not included in Game view or player builds.

## Objects and trigger events

See `TRIGGERS.md` in the package for event requests, independent portals, invisible walls, stable IDs and runtime examples. The game decides when to request an event; the integration does not wire collision callbacks.
