# CC Switch attribution

This application's Codex OAuth device login, refresh and model-discovery request contract,
and its Anthropic/Responses request and streaming conversion were adapted from:

- Project: CC Switch, https://github.com/farion1231/cc-switch
- Copyright (c) 2025 Jason Young
- License: MIT; the complete copyright and license are in LICENSE-CC-SWITCH.txt.
- Upstream revision: a4d07f313c783b0b16598c135d278a68d7653530
- References: src-tauri/src/proxy/providers/codex_oauth_auth.rs,
  src-tauri/src/services/codex_oauth_models.rs,
  src-tauri/src/proxy/providers/transform_responses.rs,
  src-tauri/src/proxy/providers/streaming_responses.rs.

Adaptations in Claude Link Lite include porting the scoped functionality from Rust to C#,
Windows DPAPI storage, integration with the local graphical provider interface, and
Code session synchronization. This application is a separate local tool, not an official
CC Switch or OpenAI distribution. Other CC Switch features have not been included.

# Claude import from Codex attribution

The incremental Codex-to-Claude transcript bridge design was informed by
claudeimportfromcodex, https://github.com/FredrikAhman/claudeimportfromcodex.
Copyright (c) 2026 FredrikAhman. MIT license text is included in
LICENSE-CLAUDEIMPORTFROMCODEX.txt.

Claude Link Lite implements the bridge independently in C#, with Windows desktop
organization selection, bidirectional checkpoints, conflict handling, provenance
markers, transactional backups, and native Codex app-server readback. It does not
install or execute the upstream Python scripts.
