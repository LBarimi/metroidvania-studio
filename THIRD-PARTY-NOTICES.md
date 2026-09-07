# Third-party notices

Project-owned code, documentation, sample assets, and engine integrations are
licensed under the MIT License in `LICENSE`. Third-party components retain their
own licenses. This inventory does not replace their license texts or notices,
and it does not change the ownership or licensing of maps and assets you import.

## Build and runtime tools

- **Node.js** is used to build the web UI and run development tools. It is
  installed separately and is not included in the web or engine packages.
  Node.js uses the MIT License and includes dependencies under additional
  licenses: [upstream license and notices](https://github.com/nodejs/node/blob/main/LICENSE).
- **.NET and ASP.NET Core** provide the server and launcher runtime. The web
  build requires a separately installed runtime. These projects use the MIT
  License, with additional notices for their dependencies:
  [.NET license](https://github.com/dotnet/runtime/blob/main/LICENSE.TXT),
  [.NET third-party notices](https://github.com/dotnet/runtime/blob/main/THIRD-PARTY-NOTICES.TXT),
  [ASP.NET Core license](https://github.com/dotnet/aspnetcore/blob/main/LICENSE.txt),
  [ASP.NET Core third-party notices](https://github.com/dotnet/aspnetcore/blob/main/THIRD-PARTY-NOTICES.txt).
  If distributing runtime or SDK components with an application, retain the
  license and notice files supplied with those exact components.

## SDL integration

The SDL source package contains project-owned integration and sample code; it
does not include SDL source or binaries. Its CMake build uses an installed SDL3
package or downloads SDL3 as a build dependency. SDL3 uses the zlib License:
[SDL license](https://www.libsdl.org/license.php).

Preserve the SDL license in redistributed source, identify any source changes,
and do not misrepresent its origin. When distributing compiled applications,
review the licenses of the components actually included. The SDL package also
includes its integration-specific `THIRD-PARTY-NOTICES.txt`.

## Optional Windows desktop application

The desktop host uses the **Microsoft WebView2 SDK**, whose SDK license is
BSD 3-Clause, together with the SDK's accompanying third-party notices. The
WebView2 Runtime is installed separately and has its own distribution terms:
[SDK license](https://www.nuget.org/packages/Microsoft.Web.WebView2/1.0.4191.47/License),
[WebView2 distribution documentation](https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/distribution).

Desktop copies retain the SDK's `LICENSE.txt` and `NOTICE.txt`, and applicable
.NET component notices, under `app/notices`. Keep these files with redistributed
copies. The project MIT License does not replace Microsoft or other third-party
terms. The desktop application is not included in the web or engine packages.

## Engine SDKs

Unity, Godot, and Unreal Engine are installed separately. Their engine SDKs and
binaries are not included in the integration packages. The MIT License covers
this project's integration code; using or redistributing an engine remains
subject to that engine's own license.

## Redistributing packages

Keep `LICENSE` and this file with source and web build copies. Engine archives
also include these documents inside installed addon or plugin folders; Unity
imports them as `LICENSE.txt` and `THIRD-PARTY-NOTICES.txt`. Preserve any separate
third-party notices when adding or bundling dependencies.
