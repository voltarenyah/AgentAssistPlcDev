# Workbench Project Storage — Future UI Changes

## Status

Deferred. The first implementation phase covers the workbench storage domain, Git worktrees, device source lifecycle, knowledge updates, backend orchestration, and backend APIs. It does not modify the React Studio.

## UI entry points

The future Studio update should consume the backend APIs from `workbench-project-storage-implementation.md` and replace the current project-name/export-root model with:

```text
selected workbench -> selected worktree -> selected device
```

The hierarchy should be visible as:

```text
Workbench
  Worktree [branch]
    Device [knowledge state, modified-file count]
      Blocks
```

Changing a parent selection must clear stale child selections and reload all device-scoped paths and data.

## Create workbench dialog

Replace “Create project from session” with “Create workbench project.” The dialog needs:

- workbench name;
- default path preview: `%LOCALAPPDATA%\AutomationWorkbench\Project\<sanitized-name>`;
- optional custom root selector;
- connected TIA session/project selector;
- inline validation for invalid names, inaccessible paths, and non-empty conflicts.

After successful creation, select the `master` worktree and first discovered device.

## Worktree controls

The Git panel needs:

- worktree list with branch and current commit;
- “New worktree” action with name, branch, and start commit;
- shared commit history;
- merge-to-master action;
- dirty-target and merge-conflict reporting;
- clear distinction between the bare shared repository and complete checked-out worktrees.

## Device refresh flow

Refresh is a two-stage UI operation:

1. Stage the complete PLC export and request a reconciliation preview.
2. Present added, changed, removed, and unchanged counts.

Changed and removed files require explicit user confirmation. The approval request must send the preview ID and approved removal paths. The result must show:

- reconciled file counts;
- automatic commit SHA;
- “files updated, commit failed” recovery state when applicable;
- stale-preview errors that require regenerating the preview.

## Modified-source experience

Source views must resolve the effective source, showing modified overlay content when present and exported baseline otherwise. The UI should:

- label baseline versus modified files;
- show the worktree’s total touched-file count;
- prepare the sparse overlay before editing;
- never offer direct edits to `exported-source`;
- retain modified files after PLC import;
- show import and compile outcomes from device metadata.

## Device knowledge state

Each device owns one database. The UI should:

- display `current`, `stale`, `missing`, or `update failed`;
- warn before graph/block-context use when stale;
- offer one “Update knowledge” action after a batch of edits;
- send all stale modified relative paths in one request;
- clear stale state only when the backend confirms that applied hashes match current overlays;
- keep failures device-scoped.

Cross-device knowledge queries are not part of the first UI update. If added later, they should explicitly select multiple device databases instead of merging their lifecycle.

## API client changes

`studio/src/api/client.ts` will need typed DTOs and functions for:

- listing/creating/selecting workbenches;
- listing/creating/selecting worktrees;
- listing/selecting devices;
- staging, previewing, and applying refresh;
- preparing an editable overlay;
- importing modified source;
- rebuilding/updating device knowledge;
- merging a feature worktree into master.

API errors should surface backend error codes and messages rather than only HTTP status.

## Likely files

- `studio/src/api/client.ts`
- `studio/src/studio/MainStudio.tsx`
- `studio/src/studio/BlockSourceView.tsx`
- `studio/src/studio/panels/GitPanel.tsx`

Before implementation, split the large `MainStudio.tsx` workbench tree, creation dialog, and refresh dialog into focused components if the backend contracts are stable enough to make those boundaries clear.

## Future UI verification

- Workbench creation with default and custom roots.
- Parent/child selection clearing.
- Complete worktree and device hierarchy rendering.
- Refresh preview and confirmation.
- Stale-preview recovery.
- Modified-source labels and touched-file count.
- Device-scoped knowledge stale/update behavior.
- Import/compile result display.
- Feature-to-master merge and conflict reporting.
- `npm run lint` and `npm run build`.
