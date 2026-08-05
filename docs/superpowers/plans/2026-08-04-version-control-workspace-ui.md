# Version Control Workspace UI Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the raw Git file panel with a PLC-object workspace for manual commits, TIA comparison, feature import/validation, evidence-rich history, and a contextual right-side details dock.

**Architecture:** A worktree-level `VersionControlPanel` owns loading and operation state; small tab components render Changes, Compare with TIA, and History. Selection is lifted to `MainStudio`, which renders `VersionControlDetailsDock` in the existing right dock. Pure presentation/state helpers are tested separately from API-bound components.

**Tech Stack:** React 19, TypeScript 6, Vite, Vitest, Testing Library, existing Radix-style UI components, Lucide icons, ASP.NET JSON API.

---

## Execution relationship

This is plan 4 of 4. It requires the worktree-level APIs and contracts from plans 1–3. Do not preserve the old stage/unstage UX or the old manifest-based `CompareProject` view.

## File responsibility map

- `studio/src/studio/version-control/versionControlState.ts`: pure grouping, labels, selection, and readiness helpers.
- `studio/src/studio/version-control/VersionControlPanel.tsx`: top status, tabs, loading, refresh, and operation orchestration.
- `studio/src/studio/version-control/VersionControlChanges.tsx`: grouped PLC objects, selection, and manual commit.
- `studio/src/studio/version-control/VersionControlCompare.tsx`: fast gate, TIA differences, accept/ignore, and feature import plan.
- `studio/src/studio/version-control/VersionControlHistory.tsx`: commit history, validation state, and rollback-feature action.
- `studio/src/studio/version-control/VersionControlDetailsDock.tsx`: object/commit/evidence details and normalized XML diff.
- `studio/src/studio/version-control/FeatureValidationDialog.tsx`: import session, compile outcome, machine confirmation, and final merge.
- `studio/src/studio/MainStudio.tsx`: active Version Control selection and right-dock composition.
- `studio/src/api/client.ts`: final typed requests for all workflow endpoints.
- `studio/src/studio/panels/GitPanel.tsx`: delete after replacement.
- `studio/src/studio/CompareProject.tsx`: remove manifest-based version-control usage; retain only if another route still needs it.

### Task 1: Finalize typed client contracts and pure state helpers

**Files:**
- Modify: `studio/src/api/client.ts`
- Create: `studio/src/studio/version-control/versionControlState.ts`
- Create: `studio/src/studio/version-control/versionControlState.test.ts`

- [ ] **Step 1: Write failing state-helper tests**

```ts
import { describe, expect, it } from 'vitest'
import {
  groupSourceObjects,
  togglePath,
  validationLabel,
  mergeBlockReason,
} from './versionControlState'

describe('versionControlState', () => {
  it('groups objects by device and PLC category', () => {
    const groups = groupSourceObjects([
      object('dev-2', 'PLC_2', 'Tags', 'Tags/Inputs.xml'),
      object('dev-1', 'PLC_1', 'Block', 'Blocks/Main.xml'),
      object('dev-1', 'PLC_1', 'Udt', 'UDT/Motor.xml'),
    ])
    expect(groups.map(group => group.key)).toEqual([
      'PLC_1/Block',
      'PLC_1/Udt',
      'PLC_2/Tags',
    ])
  })

  it('toggles one repository path without changing other selections', () => {
    expect([...togglePath(new Set(['a.xml']), 'b.xml', true)]).toEqual(['a.xml', 'b.xml'])
    expect([...togglePath(new Set(['a.xml', 'b.xml']), 'a.xml', false)]).toEqual(['b.xml'])
  })

  it('distinguishes validated, unlabeled, and invalid history', () => {
    expect(validationLabel('Validated')).toBe('TIA validated')
    expect(validationLabel('Unlabeled')).toBe('Full scan required')
    expect(validationLabel('Invalid')).toBe('Validation evidence invalid')
  })

  it('returns an item reason without globally blocking unrelated imports', () => {
    expect(mergeBlockReason({ importable: false, reason: 'TIA_FEATURE_OVERLAP' })).toBe(
      'This object changed in both TIA and the feature.',
    )
    expect(mergeBlockReason({ importable: true, reason: null })).toBeNull()
  })
})
```

- [ ] **Step 2: Run the helper test and verify failure**

Run: `npm test -- versionControlState.test.ts`

Expected: FAIL because the module does not exist.

- [ ] **Step 3: Define final API types**

Use string unions matching backend JSON:

```ts
export type VcValidationState = 'Validated' | 'Unlabeled' | 'Invalid'
export type SourceChangeState = 'Modified' | 'Added' | 'Deleted' | 'Unauthorized'
export type ConsistencyState = 'Consistent' | 'Different' | 'ScanRequired' | 'Unavailable'

export type VcSourceEntry = {
  filePath: string
  deviceId: string
  plcName: string
  category: string
  objectName: string
  state: SourceChangeState
  authorizedOnMaster: boolean
}

export type VersionControlSelection =
  | { kind: 'source'; entry: VcSourceEntry }
  | { kind: 'difference'; difference: SourceDifference }
  | { kind: 'commit'; commit: VcCommitEntry }
  | { kind: 'validation'; evidence: VcValidationEvidence }
  | null
```

Add typed functions for status, log, diff with both SHAs, selected commit, compare, accept, validate sync, import plan/import/rollback/keep, validate feature, final merge, validation evidence, and rollback feature. Every path is workbench/worktree scoped; no function accepts a raw repository path.

- [ ] **Step 4: Implement pure helpers**

Sort by PLC name, category order (`Block`, `DB`, `Udt`, `Tags`), and object name. Helpers must not mutate input arrays or sets. Map backend codes to concise user text in one place.

- [ ] **Step 5: Run helper tests and typecheck**

Run: `npm test -- versionControlState.test.ts`

Run: `npm run build`

Expected: PASS.

- [ ] **Step 6: Commit contracts and helpers**

```bash
git add studio/src/api/client.ts studio/src/studio/version-control
git commit -m "feat: model PLC version control workspace state"
```

### Task 2: Build the worktree-level panel shell and status header

**Files:**
- Create: `studio/src/studio/version-control/VersionControlPanel.tsx`
- Create: `studio/src/studio/version-control/VersionControlPanel.test.tsx`
- Modify: `studio/src/studio/MainStudio.tsx`

- [ ] **Step 1: Write a failing shell test**

```tsx
it('shows worktree role, source count, and validation state', async () => {
  mockStatus({
    branch: 'feature-a',
    role: 'Feature',
    validationState: 'Unlabeled',
    entries: [sourceEntry('PLC_1', 'Blocks/Main.xml')],
  })
  mockLog([])

  render(
    <VersionControlPanel
      workbenchId="wb-1"
      worktreeId="wt-feature"
      onSelectionChange={() => {}}
    />,
  )

  expect(await screen.findByText('feature-a')).toBeInTheDocument()
  expect(screen.getByText('Feature')).toBeInTheDocument()
  expect(screen.getByText('1 source change')).toBeInTheDocument()
  expect(screen.getByText('Full scan required')).toBeInTheDocument()
})
```

- [ ] **Step 2: Run the component test and verify failure**

Run: `npm test -- VersionControlPanel.test.tsx`

Expected: FAIL because the panel does not exist.

- [ ] **Step 3: Implement the panel shell**

Use props:

```ts
type VersionControlPanelProps = {
  workbenchId: string
  worktreeId: string
  onSelectionChange: (selection: VersionControlSelection) => void
}
```

Load worktree status and log in parallel. Render top badges for branch, role, change count, TIA consistency, and validation state. Tabs are `Changes`, `Compare with TIA`, and `History`. Keep selection when refreshing if the selected object/commit still exists; otherwise clear it. Use existing loading, error toast, theme tokens, and compact button components.

- [ ] **Step 4: Replace the old Git tab host**

In `MainStudio`, render `VersionControlPanel` for `activeTab === 'git'` using only workbench and worktree IDs. Rename the tab label from `Git worktree` to `Version control`. Do not require `selection.deviceId`, because merge and status cover every device.

- [ ] **Step 5: Run tests and build**

Run: `npm test -- VersionControlPanel.test.tsx MainStudio.contract.test.ts`

Run: `npm run build`

Expected: PASS.

- [ ] **Step 6: Commit the workspace shell**

```bash
git add studio/src/studio/version-control studio/src/studio/MainStudio.tsx
git commit -m "feat: add PLC version control workspace shell"
```

### Task 3: Implement selected-object commits without staging UI

**Files:**
- Create: `studio/src/studio/version-control/VersionControlChanges.tsx`
- Create: `studio/src/studio/version-control/VersionControlChanges.test.tsx`
- Modify: `studio/src/studio/version-control/VersionControlPanel.tsx`

- [ ] **Step 1: Write failing commit interaction tests**

```tsx
it('commits exactly the selected PLC objects', async () => {
  const user = userEvent.setup()
  const commit = vi.spyOn(api, 'commitVcPaths').mockResolvedValue({
    sha: 'abc', message: 'change A', files: ['devices/PLC_1/source/Blocks/A.xml'],
  })

  renderChanges([
    sourceEntry('PLC_1', 'Blocks/A.xml'),
    sourceEntry('PLC_1', 'Blocks/B.xml'),
  ])

  await user.click(screen.getByRole('checkbox', { name: /A/i }))
  await user.type(screen.getByLabelText('Commit message'), 'change A')
  await user.click(screen.getByRole('button', { name: 'Commit selected' }))

  expect(commit).toHaveBeenCalledWith(
    'wb-1',
    'wt-1',
    ['devices/PLC_1/source/Blocks/A.xml'],
    'change A',
  )
})

it('shows unauthorized master changes but does not select them', async () => {
  renderChanges([sourceEntry('PLC_1', 'Blocks/A.xml', { state: 'Unauthorized' })])
  expect(screen.getByText('Direct master edit')).toBeInTheDocument()
  expect(screen.getByRole('checkbox', { name: /A/i })).toBeDisabled()
})
```

- [ ] **Step 2: Run tests and verify failure**

Run: `npm test -- VersionControlChanges.test.tsx`

Expected: FAIL because the changes component does not exist.

- [ ] **Step 3: Implement Changes**

Group rows by device and object category. Each row shows checkbox, object icon/name, state badge, and secondary PLC path. Clicking the row selects it for the right dock; clicking the checkbox only changes commit scope. Add group and all-visible selection controls.

The commit area has one message field and `Commit selected (N)` button. Disable when no selected paths, blank message, operation running, or any selected master entry is unauthorized. On success, clear committed paths/message, refresh status/history, and keep unselected changes visible. Do not render `Stage`, `Unstage`, or destructive `Restore` controls.

- [ ] **Step 4: Add safe unauthorized-change actions**

For an unauthorized master object, show `Move to feature` and `Discard` in the right dock rather than inline. Both require confirmation; `Discard` uses the backend confirmation flow and never masquerades as unstage.

- [ ] **Step 5: Run tests and build**

Run: `npm test -- VersionControlChanges.test.tsx VersionControlPanel.test.tsx`

Run: `npm run build`

Expected: PASS.

- [ ] **Step 6: Commit manual commit UX**

```bash
git add studio/src/studio/version-control
git commit -m "feat: commit selected PLC objects from the UI"
```

### Task 4: Implement checksum comparison and selective TIA acceptance

**Files:**
- Create: `studio/src/studio/version-control/VersionControlCompare.tsx`
- Create: `studio/src/studio/version-control/VersionControlCompare.test.tsx`
- Modify: `studio/src/studio/version-control/VersionControlPanel.tsx`

- [ ] **Step 1: Write failing fast-gate and difference tests**

```tsx
it('shows a successful fast gate without an object table', async () => {
  vi.spyOn(api, 'compareWorkbenchWithTia').mockResolvedValue(
    comparison({ fastGatePassed: true, state: 'Consistent', differences: [] }),
  )

  renderCompare()
  await userEvent.click(screen.getByRole('button', { name: 'Compare now' }))

  expect(await screen.findByText('All device checksums match')).toBeInTheDocument()
  expect(screen.queryByRole('table')).not.toBeInTheDocument()
})

it('accepts only selected TIA differences into master', async () => {
  const accept = vi.spyOn(api, 'acceptTiaDifferences').mockResolvedValue({
    pendingPaths: ['devices/PLC_1/source/Blocks/A.xml'],
  })
  renderCompare(comparison({ differences: [difference('A.xml'), difference('B.xml')] }))

  await userEvent.click(screen.getByRole('checkbox', { name: /A/i }))
  await userEvent.click(screen.getByRole('button', { name: 'Accept selected into master' }))

  expect(accept).toHaveBeenCalledWith('wb-1', expect.any(String), [
    'devices/PLC_1/source/Blocks/A.xml',
  ])
})
```

- [ ] **Step 2: Run tests and verify failure**

Run: `npm test -- VersionControlCompare.test.tsx`

Expected: FAIL because the comparison component does not exist.

- [ ] **Step 3: Implement master comparison**

Before comparison, show existing state and `Compare now`. During operation show progress from the operation-status endpoint. On a fast match, show a compact all-device success card and checksum details in the right dock.

For a full scan, group changed/added/deleted objects by device/category. Support checkboxes for changed/added objects; show additions/deletions as lifecycle-unsupported where required. Show fail-safe or otherwise non-exportable objects as `Source coverage unavailable` and explain that exact validation cannot be recorded for that workbench. `Accept selected into master` copies selected TIA XML but does not commit. `Ignore for now` marks local UI/session choice and explains that final validated merge remains blocked.

If all committed source becomes exact, show `Record TIA consistency` to invoke `validate-sync`. Never ask the browser to send checksum/fingerprint evidence.

- [ ] **Step 4: Integrate feature preflight in the same tab**

On a feature worktree, `Prepare feature import` calls the import-plan endpoint after master comparison. Display each object as importable or disabled with its exact reason. One overlap disables one row; unrelated objects remain selectable.

- [ ] **Step 5: Run tests and build**

Run: `npm test -- VersionControlCompare.test.tsx VersionControlPanel.test.tsx`

Run: `npm run build`

Expected: PASS.

- [ ] **Step 6: Commit compare and selective acceptance UI**

```bash
git add studio/src/studio/version-control
git commit -m "feat: compare PLC objects with TIA"
```

### Task 5: Implement feature import, compile decision, and validated merge flow

**Files:**
- Create: `studio/src/studio/version-control/FeatureValidationDialog.tsx`
- Create: `studio/src/studio/version-control/FeatureValidationDialog.test.tsx`
- Modify: `studio/src/studio/version-control/VersionControlCompare.tsx`

- [ ] **Step 1: Write failing workflow tests**

```tsx
it('imports selected objects and leaves failed objects independently visible', async () => {
  vi.spyOn(api, 'importFeatureObjects').mockResolvedValue(importSession([
    outcome('A.xml', 'Imported'),
    outcome('B.xml', 'Failed', 'TIA editor is open'),
  ]))

  renderDialog(importPlan(['A.xml', 'B.xml']))
  await userEvent.click(screen.getByRole('button', { name: 'Import selected' }))

  expect(await screen.findByText('Imported')).toBeInTheDocument()
  expect(screen.getByText('TIA editor is open')).toBeInTheDocument()
  expect(screen.getByRole('button', { name: 'Continue testing' })).toBeEnabled()
})

it('offers keep or rollback after compilation failure', async () => {
  vi.spyOn(api, 'validateFeatureMerge').mockResolvedValue(
    validation({ state: 'CompileFailed' }),
  )

  renderImportedDialog()
  await userEvent.click(screen.getByRole('button', { name: 'Compile all devices' }))

  expect(await screen.findByRole('button', { name: 'Keep imported objects' })).toBeEnabled()
  expect(screen.getByRole('button', { name: 'Rollback selected objects' })).toBeEnabled()
  expect(screen.queryByRole('button', { name: 'Merge to master' })).not.toBeInTheDocument()
})

it('merges only with machine confirmation and a server validation id', async () => {
  const merge = vi.spyOn(api, 'mergeValidatedFeature').mockResolvedValue({
    sha: 'merge-sha', validationState: 'Validated',
  })
  renderReadyDialog({ validationId: 'validation-1' })

  await userEvent.click(screen.getByLabelText('Machine validation completed'))
  await userEvent.click(screen.getByRole('button', { name: 'Merge validated feature' }))

  expect(merge).toHaveBeenCalledWith('wb-1', 'validation-1')
})
```

- [ ] **Step 2: Run tests and verify failure**

Run: `npm test -- FeatureValidationDialog.test.tsx`

Expected: FAIL because the dialog does not exist.

- [ ] **Step 3: Implement import session presentation**

Use one row per selected object with `Pending`, `Imported`, `Failed`, `Kept`, or `Rolled back`. Allow retry through a fresh preflight; do not silently reuse stale plans. Keep and rollback actions operate on selected successful objects.

- [ ] **Step 4: Implement full validation readiness**

The dialog shows every device with compile state and checksum. The machine-validation checkbox is explicit and records the current Git user identity. When backend state is `Ready`, show candidate master/source SHAs and `Merge validated feature`. If a branch moves, close readiness and require a new validation.

- [ ] **Step 5: Run tests and build**

Run: `npm test -- FeatureValidationDialog.test.tsx VersionControlCompare.test.tsx`

Run: `npm run build`

Expected: PASS.

- [ ] **Step 6: Commit validated feature workflow UI**

```bash
git add studio/src/studio/version-control
git commit -m "feat: guide validated PLC feature merges"
```

### Task 6: Build evidence-rich history and rollback feature creation

**Files:**
- Create: `studio/src/studio/version-control/VersionControlHistory.tsx`
- Create: `studio/src/studio/version-control/VersionControlHistory.test.tsx`
- Modify: `studio/src/studio/version-control/VersionControlPanel.tsx`

- [ ] **Step 1: Write failing history tests**

```tsx
it('shows changed PLC objects and validation state for each commit', async () => {
  renderHistory([
    commit({
      sha: 'abc',
      files: ['devices/PLC_1/source/Blocks/Main.xml'],
      validationState: 'Validated',
      evidenceKind: 'feature-merge',
    }),
  ])

  expect(screen.getByText('Main')).toBeInTheDocument()
  expect(screen.getByText('TIA validated')).toBeInTheDocument()
  expect(screen.getByText('Feature merge')).toBeInTheDocument()
})

it('creates a rollback feature instead of restoring master', async () => {
  const create = vi.spyOn(api, 'createRollbackFeature').mockResolvedValue(worktree('rollback-main'))
  renderHistory([commit({ sha: 'abc', files: ['devices/PLC_1/source/Blocks/Main.xml'] })])

  await userEvent.click(screen.getByText('Main'))
  await userEvent.click(screen.getByRole('button', { name: 'Create rollback feature' }))

  expect(create).toHaveBeenCalledWith('wb-1', 'abc', [
    'devices/PLC_1/source/Blocks/Main.xml',
  ], expect.any(String))
  expect(screen.queryByText('Reset master')).not.toBeInTheDocument()
})
```

- [ ] **Step 2: Run tests and verify failure**

Run: `npm test -- VersionControlHistory.test.tsx`

Expected: FAIL because the history component does not exist.

- [ ] **Step 3: Implement History**

Render chronological commit cards with short SHA, message, author/time, changed object count, feature/merge marker, and validation badge. Expand or select a commit to list actual changed PLC objects. Clicking a commit or its evidence sends selection to the right dock.

For selected historical XML objects, offer `Create rollback feature`; collect a valid feature name, call the endpoint, then prompt the user to switch to the new worktree and commit the generated changes. Never show direct master restore/reset.

- [ ] **Step 4: Run tests and build**

Run: `npm test -- VersionControlHistory.test.tsx VersionControlPanel.test.tsx`

Run: `npm run build`

Expected: PASS.

- [ ] **Step 5: Commit history and rollback UX**

```bash
git add studio/src/studio/version-control
git commit -m "feat: show validated PLC history and rollback features"
```

### Task 7: Integrate the Version Control right dock

**Files:**
- Create: `studio/src/studio/version-control/VersionControlDetailsDock.tsx`
- Create: `studio/src/studio/version-control/VersionControlDetailsDock.test.tsx`
- Modify: `studio/src/studio/MainStudio.tsx`
- Modify: `studio/src/studio/MainStudio.contract.test.ts`

- [ ] **Step 1: Write failing detail-dock tests**

```tsx
it('shows PLC identity, fingerprints, reason, and normalized diff for an object', async () => {
  vi.spyOn(api, 'getVcDiff').mockResolvedValue(diffWithoutCreatedTimestamp())
  render(
    <VersionControlDetailsDock
      context={{ workbenchId: 'wb-1', worktreeId: 'wt-1' }}
      selection={{ kind: 'difference', difference: changedMain }}
    />,
  )

  expect(screen.getByText('PLC_1')).toBeInTheDocument()
  expect(screen.getByText('Program block')).toBeInTheDocument()
  expect(screen.getByText('Master fingerprint')).toBeInTheDocument()
  expect(await screen.findByText(/logic or structure changed/i)).toBeInTheDocument()
  expect(screen.queryByText(/<Created>/)).not.toBeInTheDocument()
})

it('shows every device checksum for validation evidence', () => {
  renderDock({ kind: 'validation', evidence: twoDeviceEvidence })
  expect(screen.getByText('PLC_1')).toBeInTheDocument()
  expect(screen.getByText('PLC_2')).toBeInTheDocument()
  expect(screen.getByText('Machine validated')).toBeInTheDocument()
})
```

- [ ] **Step 2: Run tests and verify failure**

Run: `npm test -- VersionControlDetailsDock.test.tsx`

Expected: FAIL because the dock does not exist.

- [ ] **Step 3: Implement contextual dock sections**

For source/difference selection show device, category, object name/path, state, base/master/feature/TIA fingerprints when available, reason, recognized safe-field summary, and normalized exact diff. For commit selection show message, author, timestamp, parents, changed objects, and validation status. For evidence show kind, confirmer/time, machine-validation state, and every device checksum.

Only render actions valid for the selection: move unauthorized master change to feature, confirmed discard, import, rollback imported object, or create rollback feature. Keep destructive confirmation in the shared confirmation UI.

- [ ] **Step 4: Make MainStudio use the detail dock for Version Control**

Lift `VersionControlSelection` state into `MainStudio`. When `activeTab === 'git'`, render `VersionControlDetailsDock` in the existing right dock instead of `SessionDock`. Closing/reopening the dock preserves selection. Switching worktree clears stale selection.

- [ ] **Step 5: Run focused UI tests**

Run: `npm test -- VersionControlDetailsDock.test.tsx MainStudio.contract.test.ts MainStudio.deviceSelect.test.tsx`

Expected: PASS.

- [ ] **Step 6: Commit right-dock integration**

```bash
git add studio/src/studio/version-control studio/src/studio/MainStudio.tsx studio/src/studio/MainStudio.contract.test.ts
git commit -m "feat: show PLC version control details in the right dock"
```

### Task 8: Remove obsolete UI and verify the complete workflow

**Files:**
- Delete: `studio/src/studio/panels/GitPanel.tsx`
- Delete or repurpose: `studio/src/studio/CompareProject.tsx`
- Modify: `studio/src/studio/MainStudio.tsx`
- Modify: `studio/src/studio/workbench/WorkbenchNavigator.tsx`
- Modify: `docs/version-control-workflow.md`
- Add: `studio/src/studio/version-control/VersionControlWorkflow.test.tsx`

- [ ] **Step 1: Add an end-to-end component workflow test**

Mock the API sequence and verify:

```ts
expect(apiSequence).toEqual([
  'status',
  'compare-tia',
  'import-plan',
  'import',
  'validate-merge',
  'merge-validated',
  'log',
])
```

The test must select one object, leave one overlapping object disabled, import, confirm machine validation, merge, and observe a `TIA validated` history entry.

- [ ] **Step 2: Remove obsolete raw Git and manifest compare surfaces**

Delete `GitPanel.tsx`. Remove stage/unstage/restore API calls and imports. Remove `CompareProject` from version-control navigation; if it remains for engineering diagnostics, rename it and ensure it no longer reads tracked manifests or competes with the Version Control compare tab.

- [ ] **Step 3: Update navigator wording and actions**

Rename worktree merge action to `Validate and merge to master`. Route it to the Version Control feature workflow rather than calling merge directly. Keep create/switch/delete worktree actions unchanged.

- [ ] **Step 4: Update the workflow document**

Document the actual UI sequence: select/commit source, compare master with TIA, import committed feature objects, compile all devices, confirm machine validation, merge with evidence, and create rollback features from history.

- [ ] **Step 5: Run all verification**

Run: `npm test` from `studio`

Run: `npm run lint` from `studio`

Run: `npm run build` from `studio`

Run: `dotnet test AgentAssistPlcDev.sln --no-restore --verbosity minimal` from the repository root.

Expected: all tests, lint, and build pass. If the existing large-manifest one-second performance assertion fails under concurrent load, rerun `tests/Agent.Tests` alone and record both results.

- [ ] **Step 6: Perform manual acceptance checks**

Use a newly created multi-device workbench and verify:

- clean status contains no `.gitignore`, device JSON, database, staging, or worktree JSON;
- Changes lists only PLC objects and selected commit leaves unselected changes;
- checksum match skips full scan;
- mismatch lists individual objects and supports selective master acceptance;
- one overlap disables one feature object only;
- compile failure offers keep/rollback and never merges;
- exact all-device validation creates a no-ff merge and evidence in History;
- right dock shows per-object and per-device evidence;
- historical recovery creates a rollback feature.

- [ ] **Step 7: Commit final UI cleanup**

```bash
git add studio docs/version-control-workflow.md
git commit -m "feat: complete PLC version control workspace"
```

## Plan 4 completion gate

The feature is complete only when the UI never presents ignored runtime files as PLC changes, never uses staging as a user concept, never allows direct master editing/restoration, and makes the checksum gate, object differences, blocked reasons, validation evidence, and rollback workflow understandable without reading raw Git internals.
