# CLI quick start

The command line creates and edits map JSON without a browser, display, game engine, or running web server. It uses the same versioned operations and Lua API as the editor.

Using a downloaded program ZIP? Start with [Headless from a release download](release-downloads.md); it includes a CLI launcher and needs no npm installation.

Install with Node.js 24+ and the .NET 10 runtime:

```sh
npm install --global metroidvania-studio
metroidvania-studio help
```

Alternatively, build once from the repository root with Node.js 24+ and the .NET 10 SDK:

```sh
node tools/scripting/build-runtime.mjs
dotnet build metroidvania-studio/cli/MetroidvaniaStudio.Cli.csproj -c Release
```

Run the resulting DLL with the .NET 10 runtime:

```sh
dotnet metroidvania-studio/cli/bin/Release/net10.0/MetroidvaniaStudio.Cli.dll help
```

The examples below use `metroidvania-studio` as shorthand for that command. An installed npm package provides this command directly. The package does not need an engine installation or an AI account.

Create a workspace directory, then a new map:

```sh
mkdir workspace
metroidvania-studio init --workspace workspace --map maps/world.json --name "My World"
```

Create `workspace/build-map.lua`:

```lua
local hall = studio.room.add {
    id = "hall", name = "Hall",
    x = 0, y = 0, width = 24, height = 12
}
studio.tiles.rectangle {
    roomId = hall, layer = "foreground",
    x = 0, y = 0, width = 24, height = 2
}
studio.room.add {
    id = "tower", name = "Tower",
    x = 24, y = 0, width = 12, height = 24
}
print("Created a hall and a tower")
```

Preview, then save:

```sh
metroidvania-studio run --workspace workspace --map maps/world.json --script build-map.lua --seed 7 --dry-run
metroidvania-studio run --workspace workspace --map maps/world.json --script build-map.lua --seed 7
metroidvania-studio inspect --workspace workspace --map maps/world.json
metroidvania-studio validate --workspace workspace --map maps/world.json
metroidvania-studio preview --workspace workspace --map maps/world.json --output previews/world.svg
metroidvania-studio export-room --workspace workspace --map maps/world.json --room hall --output exports/hall.json
```

The SVG preview shows room rectangles with region colors and white outlines. It is a headless layout overview; it does not render tile textures or reproduce the interactive minimap's entrance artwork.

All input and output filenames are relative to `--workspace`. Lua source files, batch JSON, map files, room exports, and SVG files remain inside that directory. Paths through symbolic links or junctions are rejected. Existing output files are preserved unless an export explicitly uses `--force`; map creation never overwrites a file.

To work in a live browser session, pass its local URL and use `@active` as the map:

```sh
metroidvania-studio inspect --url http://127.0.0.1:18765 --map @active
metroidvania-studio run --url http://127.0.0.1:18765 --workspace workspace --map @active --script build-map.lua --dry-run
```

Use the port shown by the web launcher. The script file is read from `--workspace`; the edit targets the document open in that browser session. Live edits share the editor undo history and refuse a conflicting document revision.

Close the web editor before editing its files directly. CLI writes use the web server's workspace lock and refuse to overwrite a live session. For connected editing, use the [live API](../api/live-api.md). `--dry-run` previews do not save files or acquire the writer lock. `--read-only` refuses writes for the entire command or MCP session.

Each command prints one JSON result. Save the `revision` from `inspect` and pass `--expected-revision` when applying a previously prepared edit. A stale revision fails without changing the map. Scripts and batches commit as one atomic file replacement; errors, cancellation, and worker limits leave the original map untouched.

See [commands and exit codes](commands.md), [Lua scripting](../scripting/quick-start.md), and [MCP setup](../mcp/setup.md).
