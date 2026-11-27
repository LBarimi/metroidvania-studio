# Metroidvania Studio

Create connected 2D worlds with Lua scripting, a headless CLI, and a local MCP server. Maps stay in your workspace as engine-neutral JSON.

The CLI and MCP server require **Node.js 24+** and the **.NET 10 runtime**. To install the published package from the npm registry, run `npm install --global metroidvania-studio`.

To build and install **1.3.5** from this source checkout, use npm and the **.NET 10 SDK**. From the repository root, run the following commands.

```sh
npm run pack:local
npm install --global ./builds/npm/metroidvania-studio-1.3.5.tgz
```

Run `help` to see the commands in your installed version. Builds advertising `generate` and `editor` can create seeded tilemaps and edit full projects over stdin. [Headless editing for local agents](../../docs/cli/headless-editor.md)

After either installation, check the installed version and create an empty map.

```sh
metroidvania-studio version
mkdir maps
metroidvania-studio --workspace ./maps init --map world.map.json
metroidvania-studio --workspace ./maps inspect --map world.map.json
```

To try a Lua edit, save the following as `maps/layout.lua`.

```lua
studio.room.add { id = "hall", name = "Hall", x = 0, y = 0, width = 16, height = 8 }
```

```sh
metroidvania-studio --workspace ./maps run --map world.map.json --script layout.lua --dry-run
metroidvania-studio --workspace ./maps run --map world.map.json --script layout.lua
```

To try the minimap editor's room chips in **1.3.5**, check that `metroidvania-studio capabilities` lists `minimap.place`. Save the [102-room example](../../docs/examples/minimap-world.json) as `maps/minimap-world.json`, then apply it to a fresh map. The six regions contain empty rooms ready for tile editing.

```sh
metroidvania-studio --workspace ./maps init --map minimap.map.json
metroidvania-studio --workspace ./maps apply --map minimap.map.json --batch minimap-world.json
```

Configure an MCP client to launch `metroidvania-studio mcp --workspace <folder>` for offline files, or `metroidvania-studio mcp --url http://127.0.0.1:18765` for the open editor. Connected edits target `@active`.

MCP uses standard input/output. The installed program does not download tools, access an external service, or send telemetry. Installing from a package registry is a separate npm operation. Resource and map paths are confined to the selected workspace. Use `--read-only` for an MCP connection that must not edit files.

Use `METROIDVANIA_STUDIO_DOTNET` to select an existing .NET host when it is not found automatically.

- [CLI guide](../../docs/cli/quick-start.md)
- [MCP setup](../../docs/mcp/setup.md)
- [Lua scripting](../../docs/scripting/quick-start.md)
- [Editing API](../../docs/api/index.md)
- [Local installation](../../docs/distribution/local-package.md)

Keep `LICENSE`, `THIRD-PARTY-NOTICES.md`, and `app/licenses` with redistributed copies.

Open `docs/site/index.html` inside the installed package for the searchable offline API reference.
