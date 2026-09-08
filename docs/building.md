# Building from source

Install Node.js 24+, .NET SDK 10, and Git. Run the build script for your platform:

| Platform | Build | Run |
| --- | --- | --- |
| Windows | platform/win/web/build.bat | platform/win/web/run.bat |
| macOS | platform/mac/build.command | platform/mac/run.command |
| Linux | platform/linux/build.sh | platform/linux/run.sh |

The scripts work from any current directory. The first build compiles the application and prepares its dependencies. Later builds compare source contents with the last successful bundle. If nothing changed, they reuse that bundle immediately. Otherwise, changed .NET projects compile incrementally, and a new complete bundle is prepared under `builds/`.

The run script performs the same source check before starting. Saving map JSON, editing workspace textures, or changing file timestamps alone does not rebuild application code. A failed or interrupted build leaves the previous successful bundle available.

## Force a full rebuild

Use this after manually changing generated build files or when you want to discard compiler reuse:

```text
platform/win/web/build.bat --rebuild
```

On macOS or Linux, pass `--rebuild` to the corresponding build script. PowerShell also accepts `-Rebuild`.

Prebuilt release downloads use their included application files and do not compile source when started.
