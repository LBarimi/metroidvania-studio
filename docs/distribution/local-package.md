# Local npm package

Use this workflow to build and test the npm package from source without publishing it. For a registry install, run `npm install --global metroidvania-studio`.

To build the package from the source repository, use Node.js 24+, npm, and the .NET 10 SDK:

```sh
node tools/scripting/build-runtime.mjs
npm run pack:local
```

The resulting archive is `builds/npm/metroidvania-studio-1.3.0.tgz`. The build checks the source version, matching npm/MCP metadata, required notices, and package contents. `builds/npm/latest.json` records the archive inventory and npm integrity hash. These are local build outputs.

Install the local archive in a separate test folder:

```sh
npm install --offline --ignore-scripts --no-audit --no-fund ./metroidvania-studio-1.3.0.tgz
node ./node_modules/metroidvania-studio/bin/metroidvania-studio.mjs version
mkdir maps
node ./node_modules/metroidvania-studio/bin/metroidvania-studio.mjs --workspace ./maps init --map world.map.json
```

A consumer needs Node.js 24+ and the .NET 10 runtime. The SDK, source repository, Git, engine packages, and browser are not required. No installation scripts run, and the package has no npm dependencies. It contains the portable compiled CLI, its managed dependencies, guides, and license notices.

The wrapper searches for a .NET 10 runtime using `METROIDVANIA_STUDIO_DOTNET`, the command path, `DOTNET_ROOT`, and the standard Windows installation location. An explicit override must point to a valid host; it never falls back silently. Arguments are forwarded directly without a shell, including spaces and Unicode paths. Standard input/output remains available to MCP, and interrupt/termination signals are forwarded to the child process.

## MCP client configuration

After local installation, configure a client to start the wrapper. Replace the relative arguments with paths appropriate to the client's working directory:

```json
{
  "mcpServers": {
    "metroidvania-studio": {
      "command": "node",
      "args": [
        "./node_modules/metroidvania-studio/bin/metroidvania-studio.mjs",
        "mcp",
        "--workspace",
        "./maps"
      ]
    }
  }
}
```

Use `--read-only` when the client should only inspect maps. The MCP server uses stdio and accesses the selected local workspace. The wrapper does not download runtimes, install packages, or contact a registry during execution.

MCP Registry publication metadata is maintained in `tools/mcp/server.json`. Its npm identifier, version, and `name` must match the staged package. The package build copies it to `server.json` inside the npm package. The root `package.json` deliberately remains `private: true`; only the package built under `builds/npm` is the distributable package.
