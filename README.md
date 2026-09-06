# MetroidvaniaStudio

A web-based 2D world editor for metroidvania games. Create connected rooms, paint tilemaps, and design minimaps with JSON export for cross-engine workflows.

The browser UI and local server run independently. Workspaces contain maps, resource definitions and textures; each game supplies its own importer for the exported JSON.

## Run on Windows

For a source checkout, install Node.js 24 or later and .NET SDK 10, then double-click `MetroidvaniaStudio/Start-MetroidvaniaStudio.bat`. The server runs in the background and the studio opens in your browser. Launching again reuses the active workspace.

For a prebuilt Windows release, extract the complete archive, install ASP.NET Core Runtime 10, then double-click the same batch file. A prebuilt release does not require Node.js or an SDK. Release archives are framework-dependent and do not bundle a runtime.

The default workspace is `.local/workspace` under the studio folder. Keep this folder when updating an extracted release, or choose a separate workspace:

```powershell
.\MetroidvaniaStudio\Start-MetroidvaniaStudio.ps1 -Project ..\MyWorkspace -OpenBrowser
```

The default address is `http://127.0.0.1:18765/`; the minimap view is `http://127.0.0.1:18765/?view=minimap`. Use `-Port` to run another workspace on a different port.

```powershell
# Verify development tools without starting a server or opening a browser.
.\MetroidvaniaStudio\Start-MetroidvaniaStudio.ps1 -CheckOnly

# Rebuild and restart, first persisting the current session.
.\MetroidvaniaStudio\Start-MetroidvaniaStudio.ps1 -Restart

# Save pending edits and shut down the matching session.
.\MetroidvaniaStudio\Stop-MetroidvaniaStudio.ps1
```

Pass the same `-Project` and `-Port` when restarting or stopping a custom workspace. The stop script verifies the recorded process and session before requesting shutdown. A failed save leaves the server running.

## Workspace and export

- `Maps/`: saved authoring documents.
- `Maps/AutoExport/`: derived JSON snapshots of rooms after editing becomes idle.
- `Maps/.Recovery/`: recovery data for an unfinished editing session.
- `.studio/catalog.json`: optional resource catalogue for this workspace.
- `Textures/`: workspace texture files referenced by the catalogue.

Project-owned examples are in `Samples/`. They provide default resources without importing another project's data. Workspace paths are independent of the application source tree. JSON keeps room positions, layers, tile materials, objects and metadata; rendering and game behavior belong to the consuming application.

## Development

```powershell
git config core.hooksPath .githooks
node Tools/Repository/check-text.mjs
node MetroidvaniaStudio/build-web.mjs --check-contracts
node MetroidvaniaStudio/build-web.mjs
dotnet run --project MetroidvaniaStudio/Core.Tests/MetroidvaniaStudio.Core.Tests.csproj
dotnet run --project MetroidvaniaStudio/Server.Tests/MetroidvaniaStudio.Server.Tests.csproj
node --test Tools/Repository/check-text.test.mjs Tools/Repository/check-boundaries.test.mjs Tools/Release/release.test.mjs
```

Browser interaction tests use disposable workspaces and a headless browser. Install Playwright separately and pass its module path if it is not available in local module resolution:

```powershell
.\MetroidvaniaStudio\Tests\Run-Browser-Interaction.ps1 -PlaywrightModule ..\BrowserTools\node_modules\playwright
```

All test fixtures, local logs and generated builds stay outside tracked source. Core and server builds use .NET 10 with no additional NuGet packages; web output uses local modules. Developer tool paths can be provided through `METROIDVANIA_STUDIO_NODE`, `METROIDVANIA_STUDIO_DOTNET`, or `DOTNET_ROOT`.

## Version and release

`version.json` is the authoritative application version, initially `0.1.0`. The JSON document format remains version 2; its schema and conventions are in `MetroidvaniaStudio/Contracts/`. Update it and the matching entry in `CHANGELOG.md` before a release. Commit reviewed changes to `main` first; release preparation rejects feature branches, pending files, stale main revisions and existing version tags.

```powershell
# Read-only release policy check; main is required.
node Tools/Release/release.mjs --dry-run

# Validate and create a local Windows archive; creates no tag and uploads nothing.
node Tools/Release/release.mjs --pack
```

The **Manual release** workflow runs only when dispatched for `main`. Its publish input defaults to false. Enabling publication runs validation and packaging, then creates `v<version>` at the selected main revision and uploads the archive. Versions below 1.0.0 are marked as prereleases. Feature pushes do not deploy or create tags. The archive includes the local server, web UI, localisation, sample resources and launch scripts; generated workspace data is excluded.

License selection is pending.

## 한국어 실행 안내

소스에서 실행하려면 Node.js 24 이상과 .NET SDK 10을 설치한 뒤 `MetroidvaniaStudio/Start-MetroidvaniaStudio.bat`을 더블클릭하세요. 백그라운드 서버가 실행되고 브라우저에 스튜디오가 열립니다. 이미 실행 중이면 현재 작업을 그대로 이어갑니다.

기본 저장 위치는 스튜디오 폴더의 `.local/workspace`입니다. 다른 작업 폴더를 쓰려면 `-Project`, 포트를 바꾸려면 `-Port`를 지정하세요. 종료 스크립트는 저장을 마친 뒤 해당 서버만 종료합니다. 배포 압축 파일을 사용할 때는 ASP.NET Core Runtime 10만 필요하며, 업데이트 전에 작업 폴더를 보존하세요.
