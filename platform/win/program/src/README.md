# Windows desktop source

This directory contains the C# WebView2 application, desktop UI extensions, and build scripts. It uses the shared server and web editor from this repository.

## Build

- Install Node.js 24+ and .NET SDK 10.
- Run `build.bat` to produce `builds/win/program/metroidvania-studio.exe` at the repository root. The first build downloads dependencies.
- Run `build.bat -IncludeRuntime` for a larger build that includes .NET.

## Run

- Keep the generated `app/` directory beside the executable.
- The default small build requires .NET 10 Desktop Runtime (x64), ASP.NET Core Runtime 10 (x64), and Microsoft WebView2 Runtime.
- The runtime-included build carries .NET with it; Microsoft WebView2 Runtime remains required.
- Running either build does not require a repository checkout or Node.js.
- Use `metroidvania-studio.exe --project <folder>` to select a workspace.
- A local build reuses `.local/workspace` when it exists. A copied application falls back to the current user's application-data workspace.
- A browser server is reused only when its workspace matches. Closing the application leaves a reused server running.
- An owned server stops after pending edits and recovery/export writes finish.

## Background validation

- `--check --result <json>` checks the package and runtime without opening a window.
- `--self-test --result <json>` uses a fresh test workspace and an invisible window; it never sends operating-system input.
- `--inspect-existing --result <json>` performs invisible, read-only validation against the matching browser server and leaves that server running.

The desktop build hides the in-page brand so the top menu starts with File. The browser build retains its existing header.

Source files are tracked in Git. Generated `bin/`, `obj/`, `builds/`, `.local/`, and validation output are excluded.
