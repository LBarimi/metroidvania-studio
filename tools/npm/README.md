# Metroidvania Studio

Create connected 2D worlds with Lua scripting, a headless CLI, and a local MCP server. Maps stay in your workspace as engine-neutral JSON.

This package contains the command-line and MCP tools. It requires **Node.js 24+** and the **.NET 10 runtime**. A .NET SDK, an engine installation, and a browser are not needed to run it.

```sh
npm install --global metroidvania-studio
metroidvania-studio version
mkdir maps
metroidvania-studio --workspace ./maps init --map world.map.json
metroidvania-studio --workspace ./maps inspect --map world.map.json
metroidvania-studio --workspace ./maps run --map world.map.json --script layout.lua --dry-run
metroidvania-studio mcp --workspace ./maps
```

MCP uses standard input/output. The installed program does not download tools, access an external service, or send telemetry. Installing from a package registry is a separate npm operation. Resource and map paths are confined to the selected workspace. Use `--read-only` for an MCP connection that must not edit files.

Use `METROIDVANIA_STUDIO_DOTNET` to select an existing .NET host when it is not found automatically.

- [CLI guide](docs/cli/quick-start.md)
- [MCP setup](docs/mcp/setup.md)
- [Lua scripting](docs/scripting/quick-start.md)
- [Editing API](docs/api/index.md)
- [Local installation](docs/distribution/local-package.md)

Keep `LICENSE`, `THIRD-PARTY-NOTICES.md`, and `app/licenses` with redistributed copies.
