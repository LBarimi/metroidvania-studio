# Automation

Metroidvania Studio 1.1 adds Lua scripts, a headless CLI, a local MCP server, and a versioned editing API. All four use the same validated operations and the existing map JSON format.

| Start here | Use it for |
| --- | --- |
| [Lua quick start](scripting/quick-start.md) | Generate or modify rooms with a script |
| [CLI quick start](cli/quick-start.md) | Edit, validate, export, and preview maps without a browser |
| [MCP setup](mcp/setup.md) | Give an agent local map editing tools |
| [Editing API](api/index.md) | Apply structured, atomic operation batches |
| [Live web API](api/live-api.md) | Work on the document currently open in the studio |
| [Local npm package](distribution/local-package.md) | Build and test an installable package from source |

## In the web editor

Open **Edit → Lua Scripts**. Write a script or load a UTF-8 `.lua` file, then select **Run**. The seed makes randomized scripts repeatable. **Dry run** validates the complete result without changing the map. **Cancel** discards an unfinished job.

A successful run becomes one Undo step and uses the existing autosave, room export, and engine synchronization flow. You can keep editing while a script runs. If the document changes first, the script result is rejected instead of overwriting your edits.

Scripts use [documented studio functions](scripting/api-reference.md), not file access or engine APIs. Read [execution limits](scripting/execution-limits.md) before generating large maps.

## Choose a document owner

Use file mode for a workspace that the web editor is not using. For a document open in the web editor, connect the CLI or MCP server with `--url` and use `--map @active`. This preserves the web editor's history and synchronization. Separate workspace copies can be edited independently.

The CLI emits JSON on stdout. MCP uses stdio; it does not expose a network port. Live mode connects only to an explicitly selected loopback URL.

## Data compatibility

Automation API version **1**, studio version **1.1.0**, and map format version **2** are separate version numbers. Existing map files and engine packages continue using [map format 2](../metroidvania-studio/contracts/FORMAT.md). Lua scripts are editing tools and are not embedded in exported game data.

See [validation commands and platform coverage](validation.md) to run the checks locally.

Install the CLI and MCP tools with `npm install --global metroidvania-studio`. Running the package requires Node.js 24+ and the .NET 10 runtime. Building or installing a local package does not publish it.
