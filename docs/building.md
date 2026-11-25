# Building from source

Use a Git checkout and install Node.js 24+, .NET SDK 10 and Git. Verify that `node --version` reports at least 24, `dotnet --list-sdks` lists a 10.0 SDK and `git --version` succeeds. The first build needs network access to fetch uncached dependencies. Run the scripts for your platform.

| Platform | Build | Run | Stop |
| --- | --- | --- | --- |
| Windows | platform/win/web/build.bat | platform/win/web/run.bat | platform/win/web/stop.bat |
| macOS | platform/mac/build.command | platform/mac/run.command | platform/mac/stop.command |
| Linux | platform/linux/build.sh | platform/linux/run.sh | platform/linux/stop.sh |

The scripts work from any current directory. The first build compiles the application and prepares its dependencies. Later builds compare source contents with the last successful bundle. If nothing changed, they reuse that bundle immediately. Otherwise, changed .NET projects compile incrementally, and a new complete bundle is prepared under `builds/`.

The run script performs the same source check before starting. Saving map JSON, editing workspace textures, or changing file timestamps alone does not rebuild application code. A failed or interrupted build leaves the previous successful bundle available.

## Force a full rebuild

On a Windows source checkout, `platform/win/web/rebuild_run.bat` stops the matching session, rebuilds and opens the new editor. If Node.js or .NET is installed in a custom location, set `METROIDVANIA_STUDIO_NODE` or `METROIDVANIA_STUDIO_DOTNET` to the full executable path before launching.

Use this after manually changing generated build files or when you want to discard compiler reuse:

```text
platform/win/web/build.bat --rebuild
```

On macOS or Linux, pass `--rebuild` to the corresponding build script. PowerShell also accepts `-Rebuild`.

Prebuilt release downloads use their included application files and do not compile source when started.
