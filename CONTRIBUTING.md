# Contributing

## Scope

The default product connects Claude Desktop's official Code sessions and Codex sessions that were already imported from Claude. Keep the sync-only workflow independent of provider switching and model inference.

## Architecture

- `SyncForm`: account selection, automatic alignment controls, and status.
- `TranscriptBridgeView`: read-only discovery, message previews, and manual actions.
- `TranscriptBridge`: source mapping, JSONL adapters, cursors, provenance, and append plans.
- `RealtimeSync`: file events, complete-turn checks, capture journals, destination leases, and retries.
- `CodexIndex`: one-shot local app-server readback; no generation requests.
- `Storage`: atomic writes, compare-and-swap checks, and transaction backups.
- `Desktop` / `Sessions`: Claude desktop organization scopes and legacy provider/session registration.
- `SelfTests` / `BridgeTests`: isolated fixtures and mock services.

## Local checks

Run the self-tests as shown in the README. They must use temporary data and clean up only their own verified fixture directories. Never use a contributor's real chat history as a test fixture.

If Codex is installed, these optional commands verify synthetic round trips using its actual local app-server:

```powershell
$exe = './src/ClaudeLinkLite/bin/Release/net8.0-windows/ClaudeLinkLite.exe'
$report = Join-Path $PWD 'index-check.json'
$process = Start-Process -FilePath $exe -ArgumentList @('--bridge-index-test', '--paginated', $report) -WindowStyle Hidden -Wait -PassThru
Get-Content $report
if ($process.ExitCode -ne 0) { throw 'Index check failed' }
```

The fixture is created beside the report. No model generation is requested. Do not run the legacy subscription inference probe when testing sync; it uses a real account and is outside this workflow.

Before shipping format changes, exercise both directions, duplicate prevention, simultaneous additions, incomplete writes, history rewrites, rollback, and native Windows writer-lock interoperability. Keep account discovery independent of private machine state.

## Pull requests and issue reports

Describe the observable behavior and the relevant checks. Report app/CLI versions and the status message; redact paths, account IDs, and conversation text. Do not attach `Data`, auth files, journals, SQLite databases, or raw chat transcripts.

Keep third-party notices when adapting existing code. Downloadable archives should include licenses and documentation and exclude all runtime data. The shipped embedded debug information must use mapped source paths rather than a developer's home directory.

