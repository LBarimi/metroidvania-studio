# Changes

## 0.1.0

- Add Godot, Unreal Engine 5 and SDL3 integration packages, local installers, portable JSON loading and native validation.

- Move Windows web build, run and stop scripts into `platform/win/web/`.

- Exclude local workflow and hook configuration from tracked source.

- Standardize source and build directories on lowercase kebab-case, keep diagnostics private, and provide one stable Unity installer filename.
- Open uniform doorways between the four icon rooms and use a white outline.

- Add a pixel-grid room symbol, File/Edit/Help menus and build information.
- Add multi-file room import and selected/all/changed room JSON saving.
- Fix whole-room clipboard, reflection, rotation and deletion; allow collision-only room joins from every tile drawing tool.
- Preview tile selections immediately without rerendering terrain on every pointer move; show contextual tools and clearer selection guidance.
- Add Japanese, Simplified Chinese, Traditional Chinese (Taiwan) and Russian alongside Korean and English.

- Add Windows, Linux and macOS build/run entry points with a shared runtime launcher, verified background sessions and safe save-before-restart.
- Prevent concurrent workspace writers and preserve case-sensitive file identities on Unix.

- Per-map PPU and reference-resolution settings with compatible JSON metadata.
- Optional Unity package with resource import, room loading, camera setup and local refresh.

- Separate local build and instant-run batch files with reusable builds and preserved workspaces.
- Independent web studio and configurable local workspaces.
- Tile painting, room layout, minimap and room JSON export.
- Shared data definitions for engine-neutral JSON workflows.
- Background launcher, saved-session shutdown and manual main release workflow.
