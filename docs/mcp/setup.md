# MCP setup

Metroidvania Studio exposes a local stdio MCP server for map inspection, batch edits, Lua scripts, JSON exports, and headless layout previews. The client starts a child process and communicates through its standard input and output. No port, HTTP endpoint, remote service, API key, or AI model is required by this server.

Install the CLI with Node.js 24+ and the .NET 10 runtime:

```sh
npm install --global metroidvania-studio
```

Alternatively, build from source using the [CLI guide](../cli/quick-start.md). Configure a client to run:

```sh
metroidvania-studio mcp --workspace workspace
```

For clients that launch npm packages through `npx`, pin the version in the configuration:

```json
{
  "mcpServers": {
    "metroidvania-studio": {
      "command": "npx",
      "args": ["-y", "metroidvania-studio@1.3.1", "mcp", "--workspace", "workspace"]
    }
  }
}
```

`npx` may download the package on first use. Clients that cannot launch Windows command shims can use `node` with the installed wrapper path, as shown in the [local package guide](../distribution/local-package.md).

Set the client's working directory so `workspace` resolves to the intended folder, or configure the workspace path explicitly in your own client settings. The directory must already exist. When running a source build directly, set `command` to `dotnet` and put the built `MetroidvaniaStudio.Cli.dll` path first in `args`.

Add `--read-only` to permit inspection, validation, previews, and dry runs while rejecting file writes. Each process has one fixed workspace root. Client root notifications cannot grant access outside it, and the server does not discover or read unrelated directories. Map paths and export paths must be relative to that workspace; traversal, symbolic links, junctions, and internal session files are rejected.

The server uses the official MCP C# SDK for protocol negotiation, JSON-RPC, tool discovery, notifications, and request cancellation. It supports the SDK's current discovery flow and compatible `initialize` clients. Each incoming JSON line is limited to 4 MiB. Send large batch workflows through files and the CLI instead of one oversized MCP message.

The MCP server performs no external network requests. Optional connected mode sends requests only to the explicitly selected loopback web editor. Your chosen AI client may send tool inputs and results to its model provider according to that client's settings. Use a local model/client configuration when all map content must remain on your machine.

Suggested workflow:

1. Call `studio_capabilities` for operation schemas.
2. Call `studio_inspect` to obtain room IDs and the current file revision, or `studio_create` for a new map.
3. Call `studio_apply` or `studio_run_lua` with that `expectedRevision` and `dryRun: true`.
4. Review the proposed layout and call again with `dryRun: false` to save.
5. Call `studio_preview` and `studio_validate`, then export the required room JSON.

To edit a document already open in the browser, configure the server with `mcp --url http://127.0.0.1:18765` instead of `--workspace`. Use the port printed by the web launcher. Pass `map: "@active"` to inspection, batch, Lua, validation, and preview tools. Revision checks prevent changes from overwriting intervening browser edits, and successful edits appear in the web undo history. The MCP client forwards cancellation to the live job. Connected mode supports these active-document tools; new map files and room exports use offline mode.

Close a web editor session before directly editing the same workspace through an offline MCP server. The shared writer lock rejects concurrent writers. Use the live web API for editing an open browser session.

See [tool reference](tools.md) and [Lua guide](../scripting/quick-start.md). Registry and npm publishing are separate release steps; a local build works before either is published.
