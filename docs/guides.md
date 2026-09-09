# Guides and reference

Create connected rooms, paint autotiled terrain, and design minimaps with Metroidvania Studio. These guides cover visual editing, portable JSON maps, and automation through Lua, the CLI, MCP, and the editing API.

## Start editing

| Guide | What you can do |
| --- | --- |
| [Sample world](sample-world.md) | Try painting, arranging rooms, and navigating the minimap |
| [Room layout](room-layout.md) | Add rooms, resize them, and move selected rooms together |
| [Tile palettes and autotiling](tilesets.md) | Import PNGs and set up 4-tile or 47-tile palettes |
| [Minimap design](minimap.md) | Set room colors, adjust outlines, and return to a room for editing |
| [Texture editing](textures.md) | Update shared PNG files and export portable resources |
| [Project bundles](project-bundles.md) | Download a map with its palettes and textures in one ZIP |
| [Game Preview](game-preview.md) | Check the selected room while editing |
| [Merge and split rooms](room-restructuring.md) | Restructure rooms while preserving world contents |
| [Objects and triggers](objects-and-triggers.md) | Paint object regions and describe triggers |

## Scripts and automation

Lua scripts, the headless CLI, the local MCP server, and the editing API use the same validated operations and map JSON format.

| Guide | Use it for |
| --- | --- |
| [Lua quick start](scripting/quick-start.md) | Generate or modify rooms with a script |
| [Headless from a download](cli/release-downloads.md) | Run commands from the extracted program download |
| [CLI quick start](cli/quick-start.md) | Edit, validate, export, and preview maps without a browser |
| [MCP setup](mcp/setup.md) | Give an agent local map editing tools |
| [Building from source](building.md) | Build, run, and refresh local web bundles |
| [Editing API](api/index.md) | Apply structured, atomic operation batches |
| [Live web API](api/live-api.md) | Work on the document currently open in the studio |
| [Local npm package](distribution/local-package.md) | Build and test an installable package from source |

## Open and save maps

Use **File → Open map** to choose a JSON map from your computer. **Save map** or **Ctrl+S** writes back to that file; **Save map as** lets you choose another name or folder. **Import map JSON** opens a copy with no link to the original file. Room JSON exports and workspace recovery continue in the background.

Chrome, Edge, and the Windows program support the file picker workflow. In browsers without a writable file picker, saving starts a JSON download using the browser's download settings. A download does not mark the document saved because the editor cannot confirm its completion.

## In the web editor

Open **File → Scripts**. Write a script or load a UTF-8 `.lua` file, then select **Run**. The seed makes randomized scripts repeatable. **Dry run** validates the complete result without changing the map. **Cancel** discards an unfinished job.

A successful run becomes one Undo step and uses the existing autosave, room export, and engine synchronization flow. You can keep editing while a script runs. If the document changes first, the script result is rejected instead of overwriting your edits.

Scripts use [documented studio functions](scripting/api-reference.md), not file access or engine APIs. Read [execution limits](scripting/execution-limits.md) before generating large maps.

## Choose a document owner

Use file mode for a workspace that the web editor is not using. For a document open in the web editor, connect the CLI or MCP server with `--url` and use `--map @active`. This preserves the web editor's history and synchronization. Separate workspace copies can be edited independently.

The CLI emits JSON on stdout. MCP uses stdio; it does not expose a network port. Live mode connects only to an explicitly selected loopback URL.

## Data compatibility

Automation API version **1**, studio version **1.3.2**, and map format version **2** are separate version numbers. Existing map files and engine packages continue using [map format 2](../metroidvania-studio/contracts/FORMAT.md). Lua scripts are editing tools and are not embedded in exported game data.

See [validation commands and platform coverage](validation.md) to run the checks locally.

Install the CLI and MCP tools with `npm install --global metroidvania-studio`. Running the package requires Node.js 24+ and the .NET 10 runtime. Building or installing a local package does not publish it.

## Browse the reference

Read the [online documentation](https://lbarimi.github.io/metroidvania-studio/), or open **Help → Documentation** in the studio for searchable offline guides. **Help → API reference** opens the editing API. The bundled pages are also available at `app/metroidvania-studio/dist/docs/index.html` inside a release download. From source, run `node tools/docs/build.mjs` and open `builds/docs/index.html`.
