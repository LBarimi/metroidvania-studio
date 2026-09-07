# Script execution limits

Scripts construct one batch of edits against a snapshot. The map is updated only after the script completes and every operation passes validation. A failed or cancelled script returns no partial document.

| Resource | Limit |
| --- | --- |
| UTF-8 script source | 64 KiB |
| Interpreter instructions | 1,000,000 |
| Interpreter execution time | 5 seconds |
| Allocations during interpretation | 64 MiB cumulative on the interpreter thread |
| Operations | 1,024 |
| Tile query window | 4,096 cells |
| Serialized operation data | 8 MiB, conservatively checked before allocation |
| Converted operation data | 200,000 values, at most 32 nested levels |
| Log output | 128 entries, 4,096 characters per entry, 16,384 characters total |
| Standard helper strings | 65,536 UTF-16 characters |
| Standard helper sequences | 100,000 elements |
| Input map | 32 MiB UTF-8 JSON |

The shared API additionally limits the complete request, visited cells, and total work. Prefer rectangle painting over one operation for each tile. For larger construction jobs, split the work into deliberate batches and inspect each result.

The runner checks instruction, time, cancellation, and allocation limits between every interpreter instruction. Standard string and table helpers check sizes before allocating. Unbounded native helpers, script callbacks inside native sorting, module loading, and host objects are not exposed.

The web and command-line hosts run Lua in a separate worker process with an overall deadline and memory limits. This also covers script parsing and unexpected interpreter failures. The interpreter library alone is **not** an operating-system sandbox: hosts embedding `LuaScriptRunner.Run` directly must provide equivalent process isolation for untrusted scripts.

Memory limits are resource controls, not a guarantee about all operating-system behavior. Scripts have no API for opening files, making network requests, starting processes, or reading environment variables. The host, not the script, selects the input and output map files.

Snapshot queries describe the input map. They do not include operations queued earlier in the same run. Editing a snapshot table has no effect on the result. Use explicit API methods to change the map.

The random seed controls `math.random`, and generated IDs are deterministic for the same input and operations. Time measurements, operating-system random sources, external modules, and `math.randomseed` are unavailable.

## Embedding and building

The C# entry point is `MetroidvaniaStudio.Scripting.LuaScriptRunner.Run(documentJson, source, seed, cancellationToken)`. It returns `DocumentJson`, `Logs`, `OperationCount`, and `Changed`. It performs no filesystem or network I/O. Always run untrusted scripts in a worker process as the shipped hosts do.

Normal build scripts prepare the interpreter automatically. When building the C# projects directly, first run:

```sh
node tools/scripting/build-runtime.mjs
```

The bootstrap downloads the exact source revision and verifies its SHA-256 against `tools/scripting/source-lock.json`. It compiles the interpreter without debug paths and checks the result before use. Source, build output, and the local package stay under ignored `.local/scripting-runtime`. Subsequent builds reuse a checksum-verified cache; a valid cache needs no download. Generated output records the SDK version and binary hashes without publishing machine paths.

The original interpreter assembly name and version are preserved. Its license and provenance files are copied with the CLI and web server. No separate Lua installation is required.
