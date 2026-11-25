# Windows program

The Windows desktop application uses C# and WebView2 to run the studio in a native window. Its source and build scripts are tracked in `src/`.

To run the Windows x64 release, extract `metroidvania-studio-win.zip` and open `metroidvania-studio.exe`. The ZIP includes .NET. Install [Microsoft Edge WebView2 Runtime](https://developer.microsoft.com/en-us/microsoft-edge/webview2/) if it is missing. [Release downloads](https://github.com/LBarimi/metroidvania-studio/releases/latest)

To build from a Git checkout on Windows, install Node.js 24+, .NET SDK 10 and Git, then run `build.bat` from this folder. The first build downloads its dependencies. The default build uses the installed x64 .NET Desktop Runtime 10 and ASP.NET Core Runtime 10. Use `build.bat -IncludeRuntime` to include .NET in the desktop application. Both builds use WebView2.

Open `builds/win/program/metroidvania-studio.exe` and keep its `app/` folder beside it. See [source and validation options](src/README.md) for runtime requirements and background checks.

Generated builds, dependency caches, local settings and workspaces remain outside Git.
