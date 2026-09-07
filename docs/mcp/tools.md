# MCP tools

| Tool | Main arguments | Behavior |
| --- | --- | --- |
| `studio_capabilities` | None | API version, operation schemas, and limits |
| `studio_create` | `map`, optional `name`, `dryRun` | Create an empty map; never replace an existing file |
| `studio_inspect` | `map` | Room/object IDs, geometry, properties, camera, and file revision |
| `studio_query_tiles` | `map`, `roomId`, `layer`, optional `x`, `y`, `width`, `height` | Read up to 4096 tile positions, including shapes and resource IDs |
| `studio_validate` | `map` | Validate the map data contract |
| `studio_apply` | `map`, `batch`, `expectedRevision`, optional `dryRun` | Apply an atomic versioned operation batch |
| `studio_run_lua` | `map`, `source`, `expectedRevision`, optional `seed`, `dryRun` | Execute bounded Lua in a worker and commit one edit |
| `studio_export_room` | `map`, `roomId`, `output`, optional `force`, `dryRun` | Export one room as an independent JSON document |
| `studio_preview` | `map` | Return a headless SVG room-layout overview without writing files |

Tool discovery includes descriptions, JSON input schemas, and read-only/destructive/idempotent annotations. All tools declare a closed workspace domain. Annotations help clients present actions; the actual path checks and read-only policy are enforced independently.

Successful calls return the same data in `structuredContent` and a JSON text content block. Domain errors set `isError: true` and return `error.code` and `error.message`. Unknown methods and invalid protocol requests use JSON-RPC errors. For bounded tile reads, see [tile queries](../api/queries.md). See the [CLI error codes](../cli/commands.md) for domain error meanings.

With `--url`, use `map: "@active"` for the currently open web document. Live inspection returns a content hash as `revision`; the client also carries the web instance and numeric document revision in each job. `studio_create` and `studio_export_room` are offline file tools and are unavailable in connected mode.

For an edit, `expectedRevision` must be the 64-character SHA-256 value returned by `studio_inspect`. A stale revision returns `conflict` and writes nothing. Read the latest state before deciding whether to retry; do not automatically replace the revision on an old destructive operation.

Batch example:

```json
{
  "name": "studio_apply",
  "arguments": {
    "map": "maps/world.json",
    "expectedRevision": "REPLACE_WITH_REVISION_FROM_INSPECT",
    "dryRun": true,
    "batch": {
      "apiVersion": 1,
      "operations": [
        { "op": "room.add", "id": "hall", "name": "Hall", "x": 0, "y": 0, "width": 24, "height": 12 }
      ]
    }
  }
}
```

Use the MCP client's cancellation action to stop an active script or batch. The worker is stopped and its uncommitted changes are discarded. A cancellation notification may suppress the original response; the connection remains usable for the next request. Cancellation received after an edit has already committed cannot undo that completed edit.

`studio_preview` returns `mimeType: "image/svg+xml"` and an SVG string. It represents the room layout and region colors without loading textures, running game logic, opening a browser, or capturing a desktop window. It does not reproduce the interactive minimap entrance styling.
