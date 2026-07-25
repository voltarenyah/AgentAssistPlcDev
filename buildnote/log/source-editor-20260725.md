# MCP SourceEditor verification — 2026-07-25

## Scope

Implemented the approved design in `buildnote/plan/mcp-sourceeditor.md`:

- `src_parse_block`
- `src_preview_edits`
- `src_apply_edits`
- `src_diff`
- `src_validate`

The server targets `net8.0`, uses stdio transport, shares the configured `PathJail`, and has no
Siemens Openness dependency.

## Implemented safety boundary

- Typed operations only: network/block Title and Comment plus `blockHeaderAuthor`,
  `blockHeaderFamily`, and `blockHeaderName`.
- Targets accept XML ID, one-based network number, or both; disagreement is rejected.
- Explicit cultures are updated or created. Omitted culture updates the first existing item.
- Preview and sibling apply refuse silent overwrite.
- In-place apply requires both `inPlace=true` and `confirmInPlace=true`.
- DTD processing and external XML resolution are disabled.
- Edits are clone-first and all-or-nothing.
- Baseline validation detects changes to logic/structure and changes to existing multilingual
  object IDs.
- `import_block` remains separate and excluded from the agent catalog.

## Automated verification

Fresh SourceEditor test run:

```text
dotnet test tests\Mcp.SourceEditor.Tests\Mcp.SourceEditor.Tests.csproj
Passed: 16, Failed: 0
```

Fresh solution build:

```text
dotnet build AgentAssistPlcDev.sln --no-restore
Build succeeded: 0 errors, 4 pre-existing warnings
```

Fresh full test run:

```text
SourceEditor:      16 passed, 0 failed
Contracts:         51 passed, 0 failed
Agent:             57 passed, 0 failed
VersionControl:    19 passed, 0 failed
Engineering:       20 passed, 0 failed
Knowledge:         87 passed, 1 failed
```

The one failure is the accepted pre-existing baseline:

`Mcp.Knowledge.Tests.ManifestImportTests.MalformedManifestReturnsManifestInvalid`

It was reproduced before SourceEditor implementation and remains out of scope.

A second verification run excluding only that named baseline test passed all 248 remaining tests.

## Independent review fixes

An independent code review found and drove test-first fixes for:

- Validation now compares existing multilingual composition/item structure, attributes, ordering,
  and IDs while allowing only template-shaped new culture items.
- Temporary output is reopened and validated before it is published or replaces the source.
- Original/output hashes remain distinct and correct for in-place writes.
- UTF-8 BOM and UTF-16 source encodings are retained.
- Safe-property changes appear in `src_diff`; missing slots and duplicate writes are rejected.
- Missing Title/Comment compositions are cloned from a matching document template.
- Duplicate MCP names across servers fail startup instead of silently changing routing.
- `/api/status` includes optional SourceEditor and VersionControl process health.

## MCP stdio verification

Command:

```powershell
node scripts\mcp-e2e.mjs `
  src\Mcp.SourceEditor\bin\Debug\net8.0\Mcp.SourceEditor.exe `
  scripts\e2e-sourceeditor.json
```

Result: exit code 0. All five tools returned `isError=false`.

- Parse found block `Main`, OB1, LAD, network IDs `3` and `8`.
- Preview created a `zh-CN` network comment.
- Diff reported only that editable comment and `protectedContentMatches=true`.
- Baseline validation returned `isValid=true` and `protectedContentMatches=true`.
- Apply created a sibling XML with an English network title and protected content unchanged.

## Remaining acceptance

Real TIA V17 acceptance is not claimed. It still requires:

1. Export a real non-safety FB/FC.
2. `vc_snapshot`.
3. Preview and review.
4. `src_validate` immediately before import.
5. Approved `import_block`.
6. Compile, re-export, and validate protected equality.

Phase 2 overall also remains open because the DeepSeek comment-generation review/apply workflow
and `llm_runs` persistence are separate work.
