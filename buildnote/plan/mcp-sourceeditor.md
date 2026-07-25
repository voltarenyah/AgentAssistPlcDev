# MCP Source Editor — precise structured XML editing (Phase 2, step 4)

**Date:** 2026-07-25  
**Status:** Approved  
**Scope:** Phase 2 structured, non-logic editing of Siemens TIA Portal V17 XML exports

## Goal

Build a standalone MCP server that edits approved titles, comments, and safe scalar properties in
TIA V17 XML precisely, while proving that PLC logic, interfaces, identifiers, and document structure
remain unchanged.

The first release supports safe structured editing rather than arbitrary XPath/XML patching. It
supports both sibling-file output and explicit in-place replacement. Sibling output is the default.

## Decisions

1. Use typed edit operations over a protected XML engine.
2. Support network and block titles/comments plus a code-defined allowlist of safe scalar
   properties.
3. Do not permit arbitrary XPath, arbitrary element names, or arbitrary attributes.
4. Accept a Siemens XML ID or displayed network number as a target. Prefer XML ID when both are
   supplied and reject disagreement or ambiguity.
5. Accept an explicit culture. When culture is omitted, update the first existing culture; if no
   culture entry exists, create `en-US`.
6. Use one edit pipeline for preview and apply. The only behavioral difference is the final write
   mode.
7. Default to a new sibling output file. Require `inPlace: true` for atomic replacement.
8. Keep the server on `net8.0`; it has no Siemens Openness dependency.

## Architecture

Add `src/Mcp.SourceEditor`, a `net8.0` stdio MCP server following the repository's Knowledge and
VersionControl server conventions. Add `tests/Mcp.SourceEditor.Tests` with real TIA V17 fixture
coverage.

The implementation is divided into focused units:

- `TiaXmlDocument` securely loads XML, retains the declaration and source encoding information,
  exposes a namespace-independent document model, and serializes atomically.
- `TiaBlockInspector` identifies the block, language, compile units, network numbers/IDs, cultures,
  and editable properties.
- `TargetResolver` resolves block or network targets. XML ID is authoritative. Network number is a
  fallback and must resolve exactly once.
- `StructuredEditEngine` applies a batch of typed operations in memory. It owns multilingual text
  selection and creation.
- `SafePropertyRegistry` is the only source of editable scalar property definitions. Each entry
  contains its owning element shape, value node/attribute, and validation rule.
- `ProtectedProjection` creates a deterministic representation of logic-bearing and structural
  content.
- `SourceValidator` checks standalone validity and, when given a baseline, proves that protected
  content did not change.
- `SourceDiff` reports requested field-level changes and structural integrity findings.
- `SourceEditorTools` validates MCP arguments and maps domain results/errors to structured MCP
  envelopes.

MCP transport code contains no XML mutation logic.

## MCP Tool Surface

Tool names follow the existing repository's `src_verb_noun` convention.

### `src_parse_block`

Read-only inspection.

Input:

```json
{
  "xmlFilePath": "C:\\exports\\Main [OB1].xml"
}
```

Output includes:

- Block name, number, type, programming language, and XML ID where available
- Networks in document order with one-based `networkNumber`, stable `xmlId`, title values, comment
  values, and available cultures
- Block-level editable fields and cultures
- Supported safe-property names
- Validation warnings

### `src_preview_edits`

Applies a batch in memory, validates it against the source baseline, and writes a new preview file.
The default path is `<source-name>.preview<extension>` in the source directory. An existing output
file is not overwritten unless `overwriteOutput` is explicitly true.

Input:

```json
{
  "xmlFilePath": "C:\\exports\\Main [OB1].xml",
  "outputFilePath": "C:\\exports\\Main [OB1].preview.xml",
  "overwriteOutput": false,
  "edits": [
    {
      "operation": "setNetworkComment",
      "target": {
        "xmlId": "42",
        "networkNumber": 3
      },
      "culture": "zh-CN",
      "value": "启动电机前检查许可条件。"
    }
  ]
}
```

Output includes the normalized edit results, output path, field-level diff, protected-integrity
result, warnings, and SHA-256 hashes of the source and output.

### `src_apply_edits`

Uses the same edit engine and validation gates as preview.

Input adds:

```json
{
  "inPlace": false
}
```

When `inPlace` is false, `outputFilePath` is required or defaults to
`<source-name>.edited<extension>`. When true, `outputFilePath` must be absent or equal to the source
path. In-place mode writes a temporary file in the same directory, reopens and validates it, then
atomically replaces the source. Any failure before replacement leaves the source untouched.

### `src_diff`

Compares two XML files. It returns:

- Editable-field changes with owner, target ID/number, culture, old value, and new value
- Added/removed editable culture entries
- Whether the protected projections match
- Protected differences when they do not match
- Source and modified SHA-256 hashes

### `src_validate`

With only `xmlFilePath`, validates supported TIA block shape, IDs, target structure, and safe
property shapes. With `baselineFilePath`, it additionally verifies that only allowlisted fields
changed.

## Edit Contract

Every edit has:

```json
{
  "operation": "setNetworkTitle",
  "target": {
    "xmlId": "42",
    "networkNumber": 3
  },
  "culture": "en-US",
  "value": "Motor permissives",
  "propertyName": null
}
```

Supported operations:

- `setNetworkTitle`
- `setNetworkComment`
- `setBlockTitle`
- `setBlockComment`
- `setSafeProperty`

`propertyName` is required only for `setSafeProperty`. `value` is always required and may be an
empty string, which means replace with empty text rather than delete the XML structure.

For network operations:

- At least one of `xmlId` or `networkNumber` is required.
- If both are supplied, both must resolve to the same compile unit.
- `xmlId` comparisons are ordinal.
- `networkNumber` is one-based document order among supported compile units.
- A missing or multiply-resolved target fails the complete batch.

For multilingual fields:

1. If `culture` is supplied, update the matching item or create it.
2. If omitted and one or more items exist, update the first item in document order.
3. If omitted and no item exists, create an `en-US` item.
4. New elements copy the namespace and Siemens composition structure from the closest existing
   title/comment example in the document. If no valid template exists, creation fails rather than
   inventing an unsupported schema shape.
5. Newly created Siemens `ID` values use a deterministic allocator that scans all existing IDs and
   selects unused values without changing existing IDs.

The initial safe-property allowlist is populated only from properties demonstrated in repository
fixtures or real exported V17 samples. A property is not editable merely because it is textual.
Adding an allowlist entry requires fixture tests and protected-projection review.

## Protected Boundary

The following are always protected:

- `FlgNet` and any other logic/source body
- Compile-unit count, order, programming language, and IDs
- Block interfaces, sections, members, data types, addresses, and start values
- Siemens object IDs except IDs allocated for newly created multilingual text structures
- References between Siemens objects
- Block type/name/number and structural containers
- Any element or attribute not named by a typed operation or `SafePropertyRegistry` entry

`ProtectedProjection` removes only the exact editable value slots and permitted newly created
multilingual scaffolding, then canonicalizes the remaining XML. Baseline and result projections
must match. This makes "only requested safe fields changed" a validation invariant rather than an
assumption.

The validator also confirms that every reported field change corresponds to a requested normalized
edit. Unexpected changes fail the operation.

## File Integrity and Security

- XML loading prohibits DTDs and external entity resolution.
- Input and output paths pass through the shared `PathJail`.
- Input files must exist and have an `.xml` extension.
- Output paths must remain within configured allowed roots.
- Preview/apply never silently overwrite an existing sibling output.
- Serialization preserves the XML declaration and uses the detected source encoding when supported.
- Newline and indentation changes are minimized, but byte identity of the whole file is not a
  requirement. Protected semantic/structural identity is required.
- Atomic write uses a temporary file in the destination directory, flushes and closes it, reopens
  it for validation, and then performs same-volume replacement.
- A batch is all-or-nothing. No output is written if any edit or validation step fails.

## Sandbox Classification

- `src_parse_block`, `src_diff`, `src_validate`: `Read`
- `src_preview_edits`: `Write`
- `src_apply_edits`: `Write`

The current sandbox policy classifies tools rather than individual argument combinations. Therefore
in-place apply remains a `Write` tool but must require a separate explicit server-side
`confirmInPlace: true` argument in addition to `inPlace: true`. The later UI workflow must still
obtain user approval before calling it. This does not authorize TIA import; `import_block` remains a
separate destructive Engineering action gated by `vc_snapshot` and validation.

Every new tool is added to `SandboxPolicy.Defaults` so unclassified-tool tests continue to fail
closed.

## Error Model

Tool failures use the repository's structured envelope:

```json
{
  "error": {
    "code": "SOURCE_TARGET_AMBIGUOUS",
    "message": "Network number 3 resolves to more than one compile unit.",
    "remediation": "Call src_parse_block and target the network by xmlId."
  }
}
```

Required codes:

- `SOURCE_PATH_DENIED`
- `SOURCE_FILE_NOT_FOUND`
- `SOURCE_OUTPUT_EXISTS`
- `SOURCE_XML_INVALID`
- `SOURCE_BLOCK_UNSUPPORTED`
- `SOURCE_TARGET_NOT_FOUND`
- `SOURCE_TARGET_AMBIGUOUS`
- `SOURCE_TARGET_MISMATCH`
- `SOURCE_OPERATION_UNSUPPORTED`
- `SOURCE_PROPERTY_UNSUPPORTED`
- `SOURCE_CULTURE_INVALID`
- `SOURCE_TEMPLATE_MISSING`
- `SOURCE_FIELD_PROTECTED`
- `SOURCE_INTEGRITY_CHANGED`
- `SOURCE_WRITE_FAILED`

Errors identify the failed batch index where applicable. No partial output is retained.

## Testing Strategy

Tests use existing TIA V17 fixtures from `tests/Mcp.Knowledge.Tests/Fixtures` through linked fixture
files or a shared fixture directory; production code is not copied from Knowledge.

Unit and integration coverage:

1. Secure loading rejects malformed XML, DTDs, and external entities.
2. Inspection returns block metadata, document-order network numbers, XML IDs, cultures, and
   editable fields for OB/FC/DB fixture variants.
3. Targeting by ID and number reaches the same network; disagreement, absence, and ambiguity fail.
4. Explicit cultures update/create correctly; omitted culture follows the approved fallback.
5. XML-reserved characters, Unicode, multiline text, empty text, and newline variants round-trip.
6. Every typed operation changes only its intended field.
7. Every protected-region mutation is detected by baseline validation and `src_diff`.
8. Multiple edits are applied in request order; duplicate writes to the same normalized field are
   rejected to avoid order-dependent surprises.
9. A failed batch produces no output.
10. Preview and apply produce the same XML for identical edits.
11. Sibling output refuses overwrite by default.
12. In-place mode requires both flags and leaves the original untouched on pre-replacement failure.
13. MCP tools return structured success and failure envelopes.
14. `SandboxPolicyTests.EveryCurrentMcpToolIsClassified` covers all five tools.

Verification commands:

```powershell
dotnet test tests\Mcp.SourceEditor.Tests\Mcp.SourceEditor.Tests.csproj
dotnet test AgentAssistPlcDev.sln
dotnet build AgentAssistPlcDev.sln
```

The real-TIA acceptance test is:

1. Export a fixture-equivalent FB/FC from TIA V17.
2. Run `vc_snapshot`.
3. Run `src_preview_edits` and review `src_diff`.
4. Run `src_validate` with the original as baseline.
5. Apply or use the approved sibling output.
6. Run `src_validate` again immediately before `import_block`.
7. Import through Engineering.
8. Re-export, confirm protected projection equality, and compile.

## Integration Boundaries

This component does not:

- Import into TIA
- Create Git snapshots
- Generate comments with DeepSeek
- Edit `FlgNet`, SCL/ST source, interfaces, tags, UDTs, or block structure
- Provide arbitrary XML/XPath patches
- Implement the Generate → review → apply UI

The later workflow composes VersionControl, SourceEditor, and Engineering in that order. Keeping
these responsibilities separate preserves standalone MCP usability and prevents SourceEditor from
gaining Openness or Git dependencies.

## Documentation Updates During Implementation

- Add both projects to `AgentAssistPlcDev.sln`.
- Update `agent.md` solution layout, MCP inventory, safety notes, and current status.
- Add a Phase 2.4 completion record to `buildnote/plan/initialLaunch_20260717.md`.
- Add a focused implementation/E2E record under `buildnote/log/`.

## Exit Criteria

The component is complete when:

1. All five MCP tools work standalone over stdio.
2. Fixture tests prove precise title/comment edits and protected-region preservation.
3. Sibling and atomic in-place output modes are tested.
4. The full solution builds and all automated tests pass.
5. MCP Inspector exercises parse, preview, diff, validate, and apply.
6. A real TIA V17 title/comment edit imports, re-exports with protected content unchanged, and
   compiles successfully.
