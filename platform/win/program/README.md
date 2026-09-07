# Windows program

The Windows desktop application uses C# and WebView2 to run the studio in a native window. Its source and build scripts are tracked in `src/`.

Install Node.js 24+ and .NET SDK 10, then run `build.bat` from this folder. The first build downloads its dependencies. The application is written to `builds/win/program/` at the repository root.

Run `build.bat -IncludeRuntime` to include .NET in the application. Microsoft WebView2 Runtime remains required.

Open `builds/win/program/metroidvania-studio.exe` and keep its `app/` folder beside it. See [source and validation options](src/README.md) for runtime requirements and background checks.

Generated builds, dependency caches, local settings and workspaces remain outside Git.
