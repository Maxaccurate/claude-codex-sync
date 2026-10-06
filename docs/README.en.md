# Claude Codex Sync

A Windows desktop tool for people who use both a Claude subscription and a ChatGPT subscription for coding.

Continue a Claude Desktop **Code** conversation in Codex when your Claude usage limit is reached, then move back to Claude when access returns. The tool appends saved visible messages and readable tool logs to the original imported conversation.

## Getting started

1. Install Claude Desktop and create a Code conversation.
2. Install Codex / a ChatGPT desktop app with Code support and sign in with your ChatGPT subscription.
3. Use Codex's official import flow to import the Claude conversation first.
4. Download the Windows x64 ZIP from [Releases](https://github.com/Maxaccurate/claude-codex-sync/releases), extract it to a writable folder, and run `ClaudeLinkLite.exe`.
5. Select your Claude account, review the detected pairs, enable automatic alignment, and keep the tool open.
6. Finish the current generation. If the tool reports that the destination is busy, exit that destination normally. Wait for the aligned status before reopening the conversation there.

The default sync flow does not route model requests or require a ChatGPT subscription inside Claude. Each official client manages its own login, model, and usage limits.

## Experimental compatibility

This is **change capture with safe catch-up**, not instant live refresh of two active official windows. Codex writer locks and running Claude processes delay destination writes. Existing imported local legacy and paginated histories are supported; unknown layouts, rewritten history, and incomplete JSONL records pause synchronization.

Images, encrypted reasoning, permissions, credentials, and executable tool state are not transferred. Tool activity is readable text. New independent conversations are not automatically paired, and deletions are not propagated.

Keep the EXE's `Data/` folder when moving the application. Its backups and message journals contain private conversations; never publish them in a repository or issue.

## Development

Build on Windows with the .NET 8 SDK:

```powershell
dotnet build src/ClaudeLinkLite/ClaudeLinkLite.csproj -c Release
dotnet publish src/ClaudeLinkLite/ClaudeLinkLite.csproj -c Release -r win-x64 --self-contained true -o dist/windows-x64
```

See [CONTRIBUTING](../CONTRIBUTING.md) for isolated tests and architecture. MIT licensed, with third-party notices retained. This is an independent project, unaffiliated with Anthropic, OpenAI, or CC Switch.
