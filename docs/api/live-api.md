# Live web API

The web server exposes automation under `/api/v1` on its existing loopback address. Use the address printed by the launcher; do not assume the default port is free.

For most clients, [CLI live mode](../cli/commands.md) or [MCP live mode](../mcp/setup.md) handles this protocol for you.

## Inspect and submit

1. `GET /api/v1/capabilities` returns the operation schema and execution limits.
2. `GET /api/v1/document` returns `apiVersion`, `instanceId`, `documentRevision`, and the active `document`.
3. `POST /api/v1/jobs` with `Content-Type: application/json` submits a Lua or batch job.
4. Poll `GET /api/v1/jobs/{id}` until its phase is terminal.

Example batch request, after substituting the identity and revision from step 2:

```json
{
  "kind": "batch",
  "clientId": "my-local-client",
  "commandId": "a-unique-command-id",
  "expectedInstanceId": "instance-from-document-response",
  "expectedDocumentRevision": 0,
  "dryRun": false,
  "batch": {
    "apiVersion": 1,
    "operations": [
      { "op": "document.update", "name": "New world" }
    ]
  }
}
```

For Lua, replace `batch` with `source`, set `kind` to `"lua"`, and optionally supply an integer `seed` (default 1).

Job envelope fields are case-sensitive. Unknown or repeated fields are rejected, including misspelled dry-run flags.

Submission returns HTTP **202** with a job object and its status URL in `Location`. Repeating the identical request with the same `clientId` and `commandId` returns the existing job while it is retained. Reusing that identity with different content is a conflict. Clients must re-inspect after completion; document revisions are not file hashes or durable identifiers.

A lost submission can also be recovered without creating a new job using `GET /api/v1/jobs/lookup?clientId=...&commandId=...`. It returns the retained status or HTTP **404**. This is useful before cancelling an uncertain submission.

## Status and cancellation

A status contains `id`, `phase`, `dryRun`, `operationCount`, `changed`, `logs`, `error`, `errorCode`, and `baseDocumentRevision`.

| Phase | Meaning |
| --- | --- |
| `running` | The isolated worker is executing |
| `completed` | The result was validated and applied, or validated as a dry run |
| `conflict` | The active document changed; nothing was applied |
| `cancelled` | Cancellation won before commit; nothing was applied |
| `failed` | Validation, execution, or a resource limit failed; nothing was applied |

Cancel with `POST /api/v1/jobs/{id}/cancel` and `{"clientId":"my-local-client"}`. Only the submitting client identity can cancel its job. A job already committed stays completed; use Undo in the editor if needed.

Jobs run outside the painting lock. The final result is applied atomically as one edit only if the same document, instance, and revision are still current. An active painting gesture blocks submission. Dry runs never change the document or its history.

## Bounds and lifetime

The host accepts at most **2 active jobs** and retains the latest **32 job records** in memory. Records and deduplication identities expire when evicted or when the server restarts. They are not a durable job queue.

Lua source is limited to **64 KiB** and HTTP batches to **8 MiB**. Workers have a **15-second** timeout, a **256 MiB managed heap** setting, and a sampled **512 MiB process memory** limit. Output is bounded. Cancellation and shutdown terminate only the worker owned by that host.

The Lua runtime also limits instructions, operations, allocations, and logs; see [execution limits](../scripting/execution-limits.md). These are resource controls, not an operating-system sandbox for running arbitrary native code.

The API keeps the existing Host/Origin checks and JSON request requirement. It is a local desktop interface, not an authenticated remote service. Client IDs support cancellation ownership and retry handling; they are not credentials. Do not forward its port or expose it through a proxy.
