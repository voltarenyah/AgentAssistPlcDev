# Offline Device Context and Explicit TIA Synchronization

## Purpose

Engineers must be able to browse and modify a previously exported PLC project without
TIA Portal running. Closing or disconnecting TIA must not remove the exported source,
modified overlays, Git history, block index, or device knowledge database.

TIA Portal is an explicit synchronization and deployment boundary. It is not the source
for ordinary device-page reads after the initial export.

## Source of Truth

Each selected device owns these persistent artifacts:

- `exported-source`: the complete, Git-tracked, last-approved PLC baseline;
- `modified-source`: the sparse, Git-tracked worktree overlay;
- `plc-knowledge.db`: a device-scoped, rebuildable derived index;
- `device.json`: persisted lifecycle and knowledge metadata;
- `staging`: temporary live-TIA exports used only for comparison.

The effective source is the overlay file when one exists and otherwise the exported
baseline file. The exported manifest, merged with the sparse overlay, is authoritative
for the offline component index. The knowledge database is never authoritative for file
existence and may be missing or stale without preventing offline source work.

No TIA disconnect, process restart, device selection, or UI reload deletes or clears any
of these persistent artifacts.

## Device Selection

Selecting a device performs a persisted-device read. It must not require or implicitly
invoke TIA Portal.

The API returns a single device snapshot containing:

- device identity and storage paths;
- the effective offline block index;
- knowledge state and last-update metadata;
- overlay count;
- TIA availability as a separate optional status.

The block index is built from `exported-source/metadata.json`. A matching
`modified-source` file replaces the baseline representation. Valid overlay-only
components are included. Missing or malformed manifests produce a visible diagnostic
instead of silently presenting a live-call failure as zero blocks.

The UI's “PLC blocks” metric and Source Overlays block browser use this offline index.
They do not call `list_blocks`. A disconnected TIA session therefore cannot erase or
visually hide the persisted index.

## Knowledge State

Knowledge state is computed when the selected-device snapshot is requested rather than
stored only in browser memory:

- `missing`: `plc-knowledge.db` does not exist;
- `stale`: the database exists and `device.json` reports stale overlays or a stale
  baseline;
- `current`: the database exists and both stale flags are false;
- `failed`: a persisted knowledge operation failure exists.

The first implementation may omit `failed` until failure metadata is persisted; it must
not infer failure from a transient UI request error.

The API also returns `updatedAt`. The UI hydrates its state from every selected-device
snapshot and refreshes that snapshot after knowledge, overlay, or baseline operations.
Browser reloads and TIA disconnects do not change the displayed state unless the
underlying database or metadata changed.

“Update changed components” updates stale overlays transactionally when possible. If
the database is missing or the baseline is stale, it automatically performs a complete
ingest. “Full device rebuild” always rebuilds from the complete effective source.
Neither operation needs a live TIA connection.

## Explicit TIA Operations

### Open project in TIA

The device overview provides an explicit action that opens or attaches to the
workbench's registered `.ap17` project. Its progress and errors are shown independently
of offline source availability. Failure leaves all persisted artifacts unchanged.

### Compare with TIA

Comparison is an explicit, non-destructive workflow:

1. Require or establish the registered TIA project connection.
2. Export the selected live PLC completely into the ignored `staging` directory.
3. Validate the staging manifest and referenced files.
4. Compare stable component identities and fingerprints against `exported-source`.
5. Show `same`, `different`, `new`, `missing`, and `unverifiable` results, including
   stored and live fingerprints where available.
6. Change neither the baseline nor the overlays until the engineer approves a separate
   action.

The existing staged refresh preview supplies most of this mechanism. The UI will name
and present it as a comparison rather than implying that staging itself refreshes the
baseline.

### Engineer decisions

From the comparison, the engineer can independently:

- approve selected live changes into the exported baseline;
- approve confirmed removals from the baseline;
- import a selected modified overlay into TIA and compile it;
- leave either side unchanged.

Applying live changes marks knowledge baseline-stale and creates the existing automatic
Git commit. Importing an overlay retains that overlay. Comparison alone creates no Git
change and does not alter knowledge state.

## UI Changes

The Device Overview shows:

- persisted/effective PLC block count;
- real-time knowledge state and last update;
- overlay count;
- offline-ready status;
- a separate TIA connection status;
- **Open project in TIA**;
- **Compare with TIA**;
- **Update knowledge**.

The current “Stage full PLC refresh” label becomes “Compare with TIA.” The resulting
dialog explains that staging is temporary and asks for approval only when applying
selected differences.

The Source Overlays page always lists the offline effective-source index. Preparing an
overlay copies the baseline once, marks knowledge stale only when persistent overlay
state changes, and never requires TIA. “Import & compile” remains explicitly
TIA-dependent.

## API and Component Boundaries

A device snapshot service owns offline index construction and knowledge-state
calculation. It reads only the selected `DeviceContext`, `device.json`, exported
manifest, overlay tree, and database file existence.

The compatibility `/api/project/info` response is extended or replaced by a typed
selected-device snapshot endpoint. Ordinary UI selection uses that endpoint. Live
engineering calls remain behind explicit open, compare, and import endpoints.

Path resolution uses the existing `WorkbenchPaths` containment checks. Manifest and
overlay identities use normalized device-relative paths and stable component identity;
display names are not storage identities.

## Error Handling

- TIA unavailable: report the TIA operation error while retaining the offline snapshot.
- Missing database: show `missing` and allow a knowledge update/rebuild.
- Stale database: show `stale`; source browsing remains available.
- Missing exported manifest: show a source-index diagnostic and zero indexed blocks,
  without claiming that the PLC itself has zero blocks.
- Malformed or ambiguous overlay: report the affected relative path and do not replace
  the corresponding baseline index entry.
- Failed comparison export: preserve the tracked baseline, overlays, knowledge DB, and
  prior usable staging data unless a validated replacement is ready.
- UI refresh failure: retain the last successful snapshot and show the error instead of
  overwriting it with empty arrays or `missing`.

## Verification

Automated tests must prove:

1. Device selection succeeds with no TIA session.
2. Exported manifest blocks appear in the offline index.
3. Matching overlays replace baseline entries and overlay-only components appear.
4. A live `list_blocks` failure cannot turn a valid offline index into zero blocks.
5. An existing current DB displays `current` after a fresh UI/API launch.
6. An existing stale DB displays `stale`.
7. A nonexistent DB displays `missing`.
8. Knowledge update/rebuild works without TIA and refreshes the snapshot state.
9. Overlay creation and approved baseline refresh mark only the selected device stale.
10. Compare exports only to staging and changes no tracked source before approval.
11. TIA open/compare/import failures leave all offline artifacts intact.
12. Device and worktree databases, indexes, and statuses remain isolated.

API host tests cover snapshot serialization and selection behavior. Agent tests cover
index merging and knowledge-state calculation. Studio tests cover snapshot hydration,
error retention, labels, and removal of implicit `list_blocks` calls. The existing
workbench lifecycle end-to-end test is extended with an offline restart scenario.

## Delivery Scope

The implementation is delivered in two coherent increments:

1. Correct offline device selection, effective-source block indexing, and real-time
   knowledge-state hydration.
2. Add the explicit Open project in TIA action and rename/refine the existing staged
   refresh workflow as Compare with TIA, including fingerprint presentation and
   independent apply/import choices.

Both increments preserve the existing device-scoped storage and Git model. No legacy
export migration is introduced by this work.
