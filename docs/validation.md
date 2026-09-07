# Validation

Run development checks from a source checkout with Node.js 24+, .NET SDK 10, and Git. All commands below run in the background; none publishes a package.

## Build and unit checks

```sh
node tools/scripting/build-runtime.mjs
node platform/shared/build.mjs
dotnet run --project metroidvania-studio/core-tests/MetroidvaniaStudio.Core.Tests.csproj -c Release
dotnet run --project metroidvania-studio/server-tests/MetroidvaniaStudio.Server.Tests.csproj -c Release
dotnet run --project metroidvania-studio/automation-tests/MetroidvaniaStudio.Automation.Tests.csproj -c Release
dotnet run --project metroidvania-studio/scripting-tests/MetroidvaniaStudio.Scripting.Tests.csproj -c Release
dotnet run --project metroidvania-studio/server-automation-tests/MetroidvaniaStudio.Server.Automation.Tests.csproj -c Release
node --test metroidvania-studio/cli-tests/cli.test.mjs metroidvania-studio/cli-tests/live-server.test.mjs
node tools/repository/check-text.mjs
```

The live-server test starts its own disposable workspace. It verifies CLI and MCP edits against the actual server, revision conflicts, the shared writer lock, automatic room exports, and Undo.

## Headless browser checks

Install Playwright in a development environment and make its module available as `playwright`, or set `PLAYWRIGHT_MODULE` to the module path. Windows defaults to an installed Edge browser; other systems default to Playwright Chromium. `METROIDVANIA_STUDIO_BROWSER_CHANNEL` overrides the channel.

```sh
node metroidvania-studio/tests/run-automation.mjs --performance
```

This command creates a disposable workspace, builds temporary web files, starts a server on an available loopback port, and closes its own processes afterward. It never uses the working map workspace.

Checks cover Lua execution, dry runs, failures, cancellation, exactly-once retry after a lost response, Undo/Redo, and JSON batches. Performance checks cover immediate brush drawing, dense tile caches, and pixel equivalence. Failures retain bounded server diagnostics in the temporary test directory.

## Local installable package

```sh
npm run pack:local
npm run test:package
```

The first command produces `builds/npm/metroidvania-studio-1.2.0.tgz`. The second installs that local archive in a fresh folder, then exercises CLI, Lua, and MCP. Package validation checks its file inventory, document links, metadata, and retained dependency notices. No post-install download or engine installation is needed.

## 1.2.0 verification

The 1.2.0 checks passed for script-library storage, revision conflicts, input validation, explicit script execution, and searchable documentation over HTTP and from local files. Existing Lua cancellation, recovery, Undo/Redo, room exports, and dense-tile rendering checks also passed.

Release downloads were extracted and their command-line launchers exercised with the included runtimes on Windows and Linux. The local npm archive passed fresh-install CLI, Lua, and MCP checks. Native macOS execution has not been verified.

## 1.1.0 verification

Windows checks passed for the existing core (41), existing server (82), editing API (30), Lua runtime (16), atomic web jobs (8), CLI/MCP process tests (14), actual live-server integration (1), and repository/build/package tooling (44).

Headless browser tests passed, including lost-response recovery and room export compatibility. Dense brush processing P95 was approximately **0.2–0.3 ms** on the development machine; 262,144-tile cache tests retained exact pixels. These figures measure the tested drawing work, not universal end-to-end input latency.

A fresh clone also built with empty NuGet and HTTP caches: the interpreter was compiled from its pinned upstream source, all four CLI dependencies were restored, and the web, launcher, server, CLI, and five engine packages built without a game project or engine installation. The package installed from that clone passed its CLI, Lua, and MCP tests.

The locally installed npm package passed on Windows and native Linux under WSL. Linux checks also verified termination of a worker when its owning process exits. macOS shell wrappers and portable managed outputs were checked, but **native macOS execution still needs verification** before claiming a tested macOS 1.1.0 release.

Release packaging checks the approved `main` commit, archive contents, version metadata, licenses, and checksums before publication. See the [publication checklist](distribution/publication.md).
