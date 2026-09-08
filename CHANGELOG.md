# Changes

## 1.3.2 (unreleased)

- Separate portals from numbered trigger events. Import independent portal detection regions and leave contact behavior to the game.
- Add translucent blue Invisible wall objects with tile-grid rectangle placement, clipping, descriptions and stable IDs. Engine packages import invisible collision geometry.
- Open the room creation menu on empty-space right clicks with every tool, including Game camera. Keep right-drag panning and in-room erase behavior.
- Leave Rooms, Selection and Game camera when choosing an editable layer, including the current layer. Select the tile brush or object placement tool so drawing in a new room does not move it.
- Preserve tile drawing tools when switching between tile layers, and keep visibility and lock controls independent of tool selection.

## 1.3.1

- Keep room movement and resize handles available with every tool, including Game camera, while preserving room locks and pending-write protection.
- Add an Always show game camera toolbar toggle to retain the camera frame while editing with other tools.
- Track each live tile change so Game Preview keeps up throughout held brush strokes. Redraw only changed regions, share existing rendering caches and refresh fully when the camera or view changes.

- Add a Pixel Perfect checkbox to Game Preview. Toggle source-pixel snapping for both camera controls while retaining room bounds, and remember the setting locally without changing map data.

- Add tile palette import and Game Preview demonstrations to the README.

- Reuse current source web builds and compile changed .NET projects incrementally. Keep a full rebuild option and preserve the previous successful bundle on failure.

- Add a Game camera tool on every layer, with a draggable white frame and crosshair synchronized with Game Preview. Clamp movement to the active room, preserve source-pixel alignment, and lock undersized axes without changing map data.

- Replace the Game view toolbar button with a collapsible Game Preview window. Maximize or restore it, pan its camera independently, and see live edits with the selected room, PPU and resolution. Share tile caches and pause hidden previews.

- Merge selected rooms from Edit or the inspector, preserving world positions, both tile layers, object IDs and compatible room properties in one Undo step.
- Split a selected rectangle into a new room and partition the remaining area without overlapping rooms. Preserve complete objects, triggers, nodes and room backdrops; reject cuts through an object and conflicting merge metadata.

- Allow room movement and resizing from foreground, background, object, trigger and decal layers.
- Draw magenta portal regions on the tile grid with click-and-drag, clipped to the active room, while preserving existing portal IDs and event settings.
- Add magenta one-shot portals and respawn points. Show Spawn, Portal, Path and Respawn point in the object palette while retaining legacy placed objects.
- Add a Once checkbox (checked by default for new triggers), 200 numbered trigger events, and descriptions displayed at the center of placed objects and trigger regions.
- Hide placement IDs in the inspector and generate compact decimal IDs for new objects and copies. Keep string IDs in JSON for compatibility, retaining existing and explicit script IDs.
- Include explicit trigger request managers for Unity, Unreal Engine 4/5, Godot and SDL, with shared event numbers, room coordinates, ID and description payloads, and one-shot reset controls.
- Preserve legacy event text and customized object definitions during catalog upgrades.
- Fix property forms rejecting unchanged objects after a repeated selection, while continuing to reject stale edits to changed objects.

## 1.3.0

- Add a tile palette editor with four-source and 47-state autotiling, separate slopes, atlas or individual PNG imports, connection previews and a default tile view.
- Include grassland, rock, ice cavern, volcanic and ancient ruins themes, with paired four-source and 47-state textures and editable Aseprite originals. New palettes default to four-source connections.
- Organize palettes into collapsible groups. Add, rename and reorder groups, move palettes between groups, and access texture folders from palette settings.
- Reload registered textures after external image saves and rebuild compatible sprite atlases while preserving map edits and custom rules.
- Select multiple rooms with Ctrl/Cmd-click and move them together, preserving their spacing with collision handling and one-step Undo. Click empty space to clear the selection.
- Place PPU and resolution presets beside Game view, including non-power-of-two PPU values. Preserve per-map settings and imported custom values.
- Use system file pickers for map opening and saving, remember the map and texture folders, and support cancelling or retrying a pending dialog.
- Keep Maps and Textures beside the application or at the source root, with storage folder shortcuts. Migrate workspace catalogs into the internal .studio folder while preserving existing data.
- Save compact UTF-8 JSON with culture-independent numbers. Update selected-room and total JSON sizes during painting and erasing without waiting for automatic export.
- Keep palette source selection separate from rule assignment, fix paired theme texture selection, and prevent color picker work from delaying pointer input.
- Add a six-room sample world and Help shortcut. Start blank maps and new room dialogs at 16 by 10 tiles.
- Rebuild outdated source web bundles automatically when running the studio. Fix the Help documentation redirect loop, use eye icons for layer visibility, and simplify the Edit menu.
- Refresh the three README demonstrations with terrain themes while preserving their room-editing choreography and playback speed.

## 1.2.1

- Add npm search keywords and preserve them when generating the published package.

## 1.2.0

- Browse saved Lua scripts and examples from File → Scripts, with workspace storage and conflict checks.
- Add searchable offline documentation, API references, and Help menu links.
- Include headless command-line launchers at the root of release downloads.
- Bring CLI and MCP setup directly below the introduction in the README.

## 1.1.0

- Add a versioned editing API shared by Lua scripts, headless commands, and local MCP tools.
- Run Lua scripts from the web editor with dry runs, cancellation, bounded workers, and one-step Undo.
- Protect live edits with document revision checks and preserve existing version-2 JSON and automatic room exports.
- Add an installable npm CLI and local stdio MCP server for map creation, inspection, batch edits, Lua scripts, exports, and layout previews.
- Include API references, script examples, and setup guides with release downloads and npm packages.
- Keep script execution bounded and workspace-scoped, with cancellation and recovery from interrupted live requests.

## 1.0.0

- Fix release web launchers when another workspace occupies the default port; remember an available loopback port for reopening and stopping the same session.

- First stable release with connected rooms, responsive tile painting, room transforms, and minimap editing.
- Export engine-neutral JSON and import rooms through Unity, Godot, Unreal Engine 4/5, and SDL3 packages.
- Add ready-to-run web, Windows, macOS, and Linux downloads with launch files at the archive root.
- Bundle the Windows x64 runtime and macOS/Linux runtimes for both ARM64 and x64; retain licenses and dependency notices.

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
