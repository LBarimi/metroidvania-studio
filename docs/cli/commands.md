# CLI commands

Offline filesystem commands require `--workspace DIRECTORY`. The directory must already exist. Source and output paths are relative to this root. Commands and options may appear in either order.

| Command | Required options | Optional options | Result |
| --- | --- | --- | --- |
| `help` | None | None | Command summary |
| `version` | None | None | Tool, API, and map format versions |
| `capabilities` | None | None | Complete operation schemas and limits |
| `init` (`create`) | `--map` | `--name`, `--dry-run` | New empty map |
| `inspect` | `--map` | None | Revision, camera, rooms, objects, region properties, shared room boundaries |
| `query-tiles` | `--map`, `--room`, `--layer` | `--x`, `--y`, `--width`, `--height` | Occupied cells in a bounded rectangle |
| `validate` | `--map` | None | Map contract validation |
| `apply` | `--map`, `--batch` | `--expected-revision`, `--dry-run` | One atomic operation batch |
| `run` | `--map`, `--script` | `--seed`, `--expected-revision`, `--dry-run` | One atomic Lua edit |
| `export-room` | `--map`, `--room`, `--output` | `--force`, `--dry-run` | One independent room JSON |
| `preview` | `--map` | `--output`, `--force`, `--dry-run` | SVG layout, inline when no output is given |
| `mcp` (`serve`) | None | `--read-only` | Local stdio MCP server |

`--url http://127.0.0.1:PORT` connects `inspect`, `query-tiles`, `validate`, `apply`, `run`, `preview`, and `mcp` to a running web editor. These commands use `--map @active`. CLI batch/script files still require an explicit `--workspace`; MCP accepts source/batches inline and needs no file root in connected mode. Live previews return SVG in JSON. `init` and `export-room` use offline mode. Localhost aliases are resolved to the loopback address directly; proxies, redirects, credentials, and nonlocal endpoints are rejected.

`--read-only` is accepted for every filesystem command. Existing map edits through `apply` and `run` are explicit in-place updates; `--force` is only for replacing an export or preview output. Outputs cannot replace their source map. A dry run returns proposed metadata without changing files. Export dry runs validate paths and conflicts without creating their output.

A batch JSON file contains an API envelope:

```json
{
  "apiVersion": 1,
  "operations": [
    { "op": "room.add", "id": "hall", "name": "Hall", "x": 0, "y": 0, "width": 24, "height": 12 },
    { "op": "tiles.rectangle", "roomId": "hall", "layer": "foreground", "x": 0, "y": 0, "width": 24, "height": 2 }
  ]
}
```

Use stable room IDs instead of names or UI selection. Coordinates use tile units with positive Y pointing up. The API rejects unknown operation fields and unsupported versions. Get exact schemas with `capabilities`.

Successful results use camelCase JSON. File revisions are lowercase SHA-256 hashes of the file bytes. A changed file produces a new revision. `--dry-run` leaves the on-disk revision unchanged and returns a `preview` summary for edits. Error results have this shape:

```json
{ "error": { "code": "conflict", "message": "The map revision does not match. Inspect the map before retrying." } }
```

| Exit code | Meaning |
| --- | --- |
| `0` | Success |
| `1` | File access, uncertain live result, or unexpected internal error |
| `2` | Invalid command, path, map, batch, or script |
| `3` | Existing output or revision conflict |
| `4` | Another editor or automation process owns the workspace |
| `5` | Input, worker time, memory, or output limit |
| `6` | Write attempted in a read-only session |
| `130` | Cancelled |

The file reader is bounded to 64 MiB; the editing API accepts map documents up to 32 MiB and operation envelopes up to 8 MiB. Lua source is limited to 64 KiB. Script-specific limits can report an invalid-script error before the worker limit is reached. Worker execution has a 15-second wall-clock limit, a 256 MiB managed heap cap, and a 512 MiB process-memory threshold sampled during execution. The process threshold is a monitored limit, not an operating-system hard allocation boundary. Only the spawned worker is terminated on timeout or cancellation.

Standard output contains the command's JSON result. MCP mode reserves standard output for protocol messages and sends startup errors to standard error. A lost live-job submission response is retried with the same command identity. If the result still cannot be confirmed, `uncertain_result` asks you to inspect the live document before another edit; a completed job is never blindly submitted under a new identity. Cancellation is also requested when a known live job loses its connection.

No command opens a network listener. `worker` is a private one-request process interface used by the CLI and web server; use `apply` or `run` for normal automation.




For source validation, build the Release server and CLI, then run:

```sh
node --test metroidvania-studio/cli-tests/*.test.mjs
node metroidvania-studio/tests/run-automation.mjs
node metroidvania-studio/tests/run-automation.mjs --performance
```

The browser runner requires Playwright and a Chromium browser. Set `PLAYWRIGHT_MODULE` when Playwright is installed outside normal module resolution. Windows defaults to the installed Edge browser; other platforms use Playwright's Chromium. `METROIDVANIA_STUDIO_BROWSER_CHANNEL` selects an installed channel. `METROIDVANIA_STUDIO_DOTNET` can select the .NET host.

The runner creates its own temporary workspace from the public sample and starts a private loopback server. It never attaches to a user's editing session. Browser execution is headless. Successful runs remove their temporary output; failed runs preserve logs under `.local/automation-validation` for investigation. The optional performance checks measure brush input and dense tile caching separately from script execution.
