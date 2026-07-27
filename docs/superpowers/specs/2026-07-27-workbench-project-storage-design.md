# Workbench Project Storage Design

## Purpose

Replace the application-wide `%LOCALAPPDATA%\PlcAiAssistant\exports` working directory with an explicit hierarchy:

```text
workbench project -> Git worktree -> engineering device -> source artifacts
```

A workbench project represents one TIA engineering project and owns a shared Git repository. Each worktree is a real Git worktree with a complete checkout and its own branch. Each device owns its exported baseline, sparse modified-source overlay, temporary export staging area, metadata, and knowledge database.

This design applies only to newly created workbench projects. Existing data under `%LOCALAPPDATA%\PlcAiAssistant\exports` is not migrated or modified.

## Terminology

- **Workbench project:** Top-level user-created container for one TIA engineering project, its shared Git history, and all linked worktrees.
- **Engineering worktree:** A complete Git checkout of the engineering source representation on a dedicated branch.
- **Device:** A PLC device discovered from the TIA engineering project.
- **Exported source:** Complete, tracked representation of the last approved PLC export for a device.
- **Modified source:** Sparse, tracked overlay containing only files touched in a worktree and intended for PLC import.
- **Staging export:** Ignored temporary output from a complete PLC export, used to calculate a safe reconciliation plan.
- **Effective source:** Modified source when an overlay file exists; otherwise the matching exported-source file.

## Storage Layout

When creating a workbench project, the user supplies a name and may choose its directory. The default directory is:

```text
%LOCALAPPDATA%\AutomationWorkbench\Project\<sanitized-workbench-name>\
```

The resulting layout is:

```text
<workbench-root>\
├── workbench.json
├── repository.git\
└── worktrees\
    ├── master\
    │   ├── worktree.json
    │   ├── .gitignore
    │   └── devices\
    │       └── <device-directory>\
    │           ├── device.json
    │           ├── plc-knowledge.db
    │           ├── exported-source\
    │           ├── modified-source\
    │           └── staging\
    └── <feature-worktree>\
        ├── worktree.json
        ├── .gitignore
        └── devices\
            └── <device-directory>\
                ├── device.json
                ├── plc-knowledge.db
                ├── exported-source\
                ├── modified-source\
                └── staging\
```

`repository.git` is a bare shared Git repository. It contains Git objects, references, and worktree administration data but no checked-out source files. Every directory below `worktrees`, including `master`, is a real linked Git worktree with a complete checkout of the tracked files on its branch.

`staging` and `plc-knowledge.db` are ignored by Git because they are replaceable derived artifacts. `worktree.json`, `device.json`, `exported-source`, and `modified-source` are tracked.

## Identity and Metadata

Display names are not storage identities. Each workbench, worktree, and device receives an immutable generated ID. Directory names are sanitized user-facing labels with collision handling. APIs and persisted relationships use IDs.

### Workbench metadata

`workbench.json` is outside the Git worktrees and records:

- schema version;
- immutable workbench ID;
- workbench display name;
- creation time;
- canonical workbench root;
- shared repository path;
- TIA engineering-project identity;
- source `.ap17` path;
- registered worktrees and their immutable IDs;
- default or active worktree ID when persisted selection is desired.

### Worktree metadata

Tracked `worktree.json` records:

- schema version;
- immutable worktree ID;
- workbench ID;
- display name;
- Git branch;
- creation time;
- base commit;
- engineering-project identity;
- source `.ap17` path;
- registered device IDs;
- last successful baseline reconciliation.

### Device metadata

Tracked `device.json` records:

- schema version;
- immutable device ID;
- worktree ID;
- PLC display name and stable engineering identity;
- component manifest and relative source identities;
- last staged export checksum and timestamp;
- last approved reconciliation commit;
- import and compile outcomes;
- knowledge database state;
- overlay hashes last applied to the knowledge database.

All metadata writes are atomic. Schema versions are explicit and readers fail clearly for unsupported versions.

## Workbench Creation

The application adds an explicit “Create workbench project” flow:

1. Ask for a workbench project name.
2. Propose `%LOCALAPPDATA%\AutomationWorkbench\Project\<sanitized-name>` as the directory.
3. Allow the user to select a different root.
4. Validate the name, canonical path, write access, and directory conflict.
5. Refuse to silently reuse a conflicting non-empty directory.
6. Create `workbench.json`.
7. Initialize `repository.git` as a bare shared repository.
8. Create the initial `master` branch and linked `worktrees\master` checkout.
9. Connect or open the source TIA engineering project.
10. Persist its stable identity and `.ap17` path.
11. Discover PLC devices and create their directories and metadata.
12. Perform the initial export through the same staging, preview, confirmation, reconciliation, and commit process used for later refreshes.

## Selection Model

Application state is explicit:

```text
active workbench -> active worktree -> active device
```

All read, edit, export, ingest, import, and Git operations must receive or resolve this complete context. Names are for display; immutable IDs select persisted objects. Runtime agent context includes the selected workbench, worktree, device, effective-source roots, and the selected device database.

## Git Model

All worktrees share `repository.git`. Creating a feature worktree creates a branch from a user-selected commit, defaulting to the current `master` commit, then creates a complete linked checkout.

Both the complete exported baseline and sparse modified overlay are tracked. Feature-worktree commits and history are immediately visible through the shared repository. A feature branch can be merged into `master` without copying files between independent repositories.

Baseline refreshes create automatic commits after the user approves reconciliation. Commit messages identify:

- workbench and worktree;
- device;
- PLC project timestamp or checksum when available;
- counts of added, changed, and removed files.

If reconciliation succeeds but the Git commit fails, the application preserves the reconciled working-tree changes and reports a recoverable “files updated, commit failed” state. It must not claim that the refresh was rolled back.

## Export and Reconciliation

Refresh operates on one device at a time:

1. Export the complete selected device into its ignored `staging` directory.
2. Validate the staging manifest and all required source files.
3. Compare staging with `exported-source` using normalized relative paths, stable component identity, and content hashes.
4. Generate an immutable reconciliation preview with added, changed, removed, and unchanged entries.
5. Present changed and removed entries to the user and require confirmation before modifying tracked baseline files. New entries appear in the same preview.
6. Before applying approval, verify that neither staging nor the baseline has changed since preview generation.
7. Copy only added or changed files into `exported-source`.
8. Delete only user-approved files confirmed as removed from the PLC.
9. Leave byte-identical files untouched.
10. Update manifests and device metadata.
11. Never modify `modified-source`.
12. Create the automatic baseline Git commit.

A failed export, invalid manifest, rejected confirmation, or stale preview leaves tracked files unchanged. Refresh is blocked if reconciliation targets have conflicting uncommitted changes. Unrelated changes that cannot be overwritten may remain in the worktree.

## Editing and Effective Source

Source reads use overlay resolution:

```text
modified-source/<relative-path> if present
otherwise exported-source/<relative-path>
```

On the first edit of an exported component, the application copies it to the same relative path below `modified-source` and edits the overlay. Later edits update that overlay file only. A newly authored component exists in `modified-source` until import and a later approved PLC export introduce it into the baseline.

Overlay files remain for the lifetime of the worktree, including after successful PLC import. They show the complete set of files touched by that worktree.

Every overlay mutation marks the device knowledge database stale. Editing does not automatically update the database.

## PLC Import

Import is device-scoped and accepts only a resolved modified-source file:

1. Resolve workbench, worktree, device, relative source path, and stable component identity.
2. Display the target PLC component and its diff from the exported baseline.
3. Require destructive-operation confirmation.
4. Import through `mcp-engineering`.
5. Compile the target.
6. Persist import and compile outcomes in device metadata.
7. Retain the overlay file.

A later export and approved reconciliation may update `exported-source` to the PLC’s new state, but it does not remove the worktree overlay.

## Device-Scoped Knowledge

Each device has an independent `plc-knowledge.db`. Cross-device references are intentionally outside the normal database lifecycle because they are uncommon and would complicate rebuilds and invalidation. Future cross-device questions may query multiple device databases explicitly.

### Full rebuild

A full device rebuild:

1. Recreates the database from that device’s complete `exported-source`.
2. Applies every file in `modified-source` as an override.
3. Records the applied overlay hashes in device metadata or database metadata.
4. Marks the database current only after all inputs are applied successfully.

### Partial overlay update

`mcp-knowledge` gains a device-scoped batched partial-update tool. It accepts the device database, manifests, and a set of modified relative paths.

For each component, the tool:

1. Resolves the old component by stable manifest identity and normalized relative path.
2. Rejects missing, renamed, or ambiguous identities.
3. Starts a database transaction.
4. Removes graph records owned by the previous component version.
5. Parses and inserts the effective modified component.
6. Updates the stored source hash.
7. Commits only when the component replacement succeeds; otherwise rolls back that component.

Multiple overlay edits are accumulated. Before the agent reuses a stale database, the application and runtime context instruct it to invoke one batched partial update. Successful application clears the stale state only if the recorded hashes still match all current overlays. Further edits mark it stale again.

## Path and Concurrency Safety

- Resolve and canonicalize every user-selected root.
- Jail paths beneath their declared workbench, worktree, and device roots.
- Reject symlink or Windows reparse-point escapes.
- Validate that a worktree is registered with the workbench’s shared repository before Git operations.
- Serialize export, reconciliation, editing, import, Git mutation, and knowledge mutation per device.
- Bind reconciliation approval to staging hashes, baseline hashes, device ID, and worktree ID.
- Reject stale approval tokens.
- Use stable relative paths and component identities instead of filename-only matching.

## Error Handling

Failures use explicit states and preserve recoverable data:

- Creation conflict: no existing non-empty directory is reused.
- Export or validation failure: tracked baseline is untouched.
- User rejection: tracked baseline is untouched.
- Stale reconciliation preview: require a new preview and approval.
- Reconciliation write failure: report the exact partial state and do not create a misleading commit.
- Git commit failure after reconciliation: retain changes and offer commit retry.
- Import or compile failure: retain overlay and record the failure.
- Knowledge parse or insert failure: roll back the affected component and keep the database stale.
- Unsupported metadata schema: refuse mutation and report the supported versions.

## Legacy Boundary

No migration is performed. Existing `%LOCALAPPDATA%\PlcAiAssistant\exports` folders remain untouched. New workbench creation, editing, Git worktree management, device knowledge, and PLC import use only the new structure.

Legacy projects may remain readable during a transition period, but no new-format metadata is written into legacy roots and no legacy root is silently adopted as a workbench.

## Verification

Automated coverage must include:

- default and custom workbench roots;
- name sanitization, collisions, inaccessible locations, and containment attacks;
- bare repository initialization and complete linked worktrees;
- shared commits, branches, history, and feature-to-master merge;
- device discovery and metadata persistence;
- staging comparison for unchanged, added, changed, removed, renamed, malformed, and rejected exports;
- approval invalidation after staging or baseline mutation;
- automatic baseline commits and recoverable commit failure;
- sparse overlay creation and effective-source resolution;
- overlay retention after import and compile;
- one independent knowledge database per device;
- full rebuild from baseline plus overlay;
- batched partial component replacement and rollback;
- stale-knowledge transitions before and after overlay changes;
- device and worktree isolation;
- API and UI workbench/worktree/device selection;
- proof that legacy export roots remain untouched.

The end-to-end acceptance flow is:

1. Create a custom-root workbench.
2. Connect one TIA engineering project containing two devices.
3. Export both devices through staging and approve the initial baseline commits.
4. Create a feature worktree and branch from `master`.
5. Edit one device through its sparse overlay.
6. Observe that only that device database becomes stale.
7. Run one batched partial knowledge update.
8. Import and compile the modified source while retaining the overlay.
9. Refresh through staging, preview, approval, reconciliation, and automatic commit.
10. Merge the feature branch into `master`.
11. Verify shared history, complete files in both worktrees, per-device database isolation, and untouched legacy exports.
