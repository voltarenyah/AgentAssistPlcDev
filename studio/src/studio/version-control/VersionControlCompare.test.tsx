// @vitest-environment happy-dom
import React, { act } from 'react'
import { createRoot } from 'react-dom/client'
import { afterEach, describe, expect, it, vi } from 'vitest'
import * as api from '@/api/client'
import VersionControlCompare from './VersionControlCompare'

globalThis.IS_REACT_ACT_ENVIRONMENT = true

const comparison = (overrides: Partial<api.WorkbenchConsistencyResult> = {}): api.WorkbenchConsistencyResult => ({
  comparisonId: 'comparison-1',
  masterSha: 'master-1',
  fastGatePassed: false,
  state: 'Different',
  liveChecksums: { 'dev-1': 'checksum-2' },
  differences: [{
    deviceId: 'dev-1',
    plcName: 'PLC_1',
    relativePath: 'devices/PLC_1/source/Blocks/Main.xml',
    identity: 'Main',
    kind: 'Changed',
    masterFingerprint: 'old',
    tiaFingerprint: 'new',
    supported: true,
  }],
  ...overrides,
})

const render = async (props: { signal?: number; mode?: 'full' | 'task'; taskId?: string | null; taskTitle?: string | null; verifyHardware?: boolean; commitMessage?: string; branch?: string; selectionResetSignal?: number; onCommitted?: () => void; onBeginOperation?: (kind: string, label: string) => string; onSelectionChanged?: (comparisonId: string | null, paths: string[]) => void; operationStatus?: api.OperationStatus | null; onComparisonBusyChanged?: (busy: boolean) => void } = {}) => {
  vi.spyOn(api, 'getWorktreeEngineeringState').mockResolvedValue({
    revision: {
      schemaVersion: 1,
      svn: { url: '^/native/main', revision: 3 },
      tia: { projectChecksum: 'dev-1:checksum-2' },
      safety: { fSignature: null },
      validation: { compileStatus: 'SUCCESS' },
    },
    svnUrl: null,
    baseSvnRevision: null,
    managedTiaProjectPath: null,
    tiaStorePath: 'C:/wb/worktrees/master/tia',
    pendingCommit: false,
  })
  const host = document.createElement('div')
  document.body.appendChild(host)
  const root = createRoot(host)
  await act(async () => root.render(
    <VersionControlCompare
      workbenchId="wb-1"
      worktreeId="wt-1"
      branch={props.branch ?? 'master'}
      signal={props.signal ?? 1}
      mode={props.mode}
      taskId={props.taskId}
      taskTitle={props.taskTitle}
      verifyHardware={props.verifyHardware}
      commitMessage={props.commitMessage ?? ''}
      selectionResetSignal={props.selectionResetSignal}
      onCommitted={props.onCommitted}
      onBeginOperation={props.onBeginOperation}
      onSelectionChanged={props.onSelectionChanged}
      operationStatus={props.operationStatus}
      onComparisonBusyChanged={props.onComparisonBusyChanged}
    />,
  ))
  return { host, root }
}

const click = async (element: Element) => {
  await act(async () => element.dispatchEvent(new MouseEvent('click', { bubbles: true })))
}

afterEach(() => {
  vi.restoreAllMocks()
  document.body.innerHTML = ''
})

describe('VersionControlCompare (inline)', () => {
  it('renders nothing until a compare is signalled', async () => {
    const compare = vi.spyOn(api, 'compareMasterWithTia').mockResolvedValue(comparison())
    const { host } = await render({ signal: 0 })

    expect(host.querySelector('[data-testid="vc-compare-result"]')).toBeNull()
    expect(compare).not.toHaveBeenCalled()
  })

  it('executes the comparison as soon as the signal arrives and lists differences', async () => {
    const compare = vi.spyOn(api, 'compareMasterWithTia').mockResolvedValue(comparison({
      timings: [{
        phase: 'plc-evidence-capture',
        purpose: 'Reading all readable block fingerprints',
        plcName: 'PLC_1',
        elapsedMilliseconds: 1430,
        outcome: '132 blocks compared',
      }],
    }))
    const { host } = await render({ signal: 1 })

    expect(compare).toHaveBeenCalledTimes(1)
    expect(host.querySelector('[data-testid="vc-compare-result"]')).toBeTruthy()
    expect(host.textContent).toContain('PLC_1 · Main')
    expect(host.textContent).toContain('PLC_1 · Main')
    expect(host.textContent).toContain('Detailed fingerprint evidence unavailable')
    expect(host.textContent).not.toContain('TIA differs from master')
    expect(host.querySelector('[data-comparison-timings]')?.textContent).toContain('Reading all readable block fingerprints')
    expect(host.querySelector('[data-comparison-timings]')?.textContent).toContain('1.4 s')
    expect(host.querySelector('[aria-label="Collapse comparison timings"]')).toBeTruthy()
  })

  it('shows live operation timings for every comparison, including while a previous result remains visible', async () => {
    const first = comparison({ differences: [], state: 'Consistent', fastGatePassed: true })
    let resolveSecond!: (value: api.WorkbenchConsistencyResult) => void
    const second = new Promise<api.WorkbenchConsistencyResult>(resolve => { resolveSecond = resolve })
    const compare = vi.spyOn(api, 'compareMasterWithTia')
      .mockResolvedValueOnce(first)
      .mockReturnValueOnce(second)
    const status: api.OperationStatus = {
      operationId: 'op-2',
      operationType: 'compare-tia',
      state: 'running',
      message: 'Exporting source objects',
      updatedAt: '2026-09-09T00:00:00Z',
      errorMessage: null,
      completedPhases: [],
      currentPhase: {
        message: 'Exporting source objects',
        startedAt: '2026-09-09T00:00:00Z',
        completedAt: null,
        elapsedMilliseconds: 1200,
      },
    }
    const { host, root } = await render({ signal: 1 })
    expect(compare).toHaveBeenCalledTimes(1)

    await act(async () => root.render(
      <VersionControlCompare
        workbenchId="wb-1"
        worktreeId="wt-1"
        branch="master"
        signal={2}
        commitMessage=""
        operationStatus={status}
      />,
    ))

    expect(compare).toHaveBeenCalledTimes(2)
    expect(host.querySelector('[data-testid="vc-compare-progress"]')).toBeTruthy()
    expect(host.textContent).toContain('Comparing the connected TIA project with master')
    expect(host.querySelector('[data-operation-timings]')).toBeTruthy()
    expect(host.querySelector('[data-testid="vc-clean-state"]')).toBeNull()
    resolveSecond(first)
    await act(async () => {})
  })

  it('collapses and reopens comparison timings without hiding the comparison result', async () => {
    vi.spyOn(api, 'compareMasterWithTia').mockResolvedValue(comparison({
      timings: [{
        phase: 'plc-evidence-capture',
        purpose: 'Reading all readable block fingerprints',
        plcName: 'PLC_1',
        elapsedMilliseconds: 1430,
        outcome: '132 blocks compared',
      }],
    }))
    const { host } = await render({ signal: 1 })
    const collapse = host.querySelector('[aria-label="Collapse comparison timings"]') as HTMLButtonElement
    expect(host.querySelector('[data-comparison-timings-list]')).toBeTruthy()

    await click(collapse)
    expect(host.querySelector('[aria-label="Expand comparison timings"]')).toBeTruthy()
    expect(host.querySelector('[data-comparison-timings-list]')).toBeNull()
    expect(host.querySelector('[data-testid="vc-compare-result"]')).toBeTruthy()

    await click(host.querySelector('[aria-label="Expand comparison timings"]')!)
    expect(host.querySelector('[data-comparison-timings-list]')).toBeTruthy()
  })

  it('labels changed fingerprint components and tag-table content hashes', async () => {
    vi.spyOn(api, 'compareMasterWithTia').mockResolvedValue(comparison({
      differences: [
        {
          deviceId: 'dev-1',
          plcName: 'PLC_1',
          relativePath: 'devices/PLC_1/source/Blocks/Main.xml',
          identity: 'Main',
          kind: 'Changed',
          masterFingerprint: 'old',
          tiaFingerprint: 'new',
          supported: true,
          fingerprintComponents: {
            Code: { stored: 'old-code', live: 'new-code', matches: false },
            Comments: { stored: 'same', live: 'same', matches: true },
          },
        },
        {
          deviceId: 'dev-1',
          plcName: 'PLC_1',
          relativePath: 'devices/PLC_1/source/Tags/Plant.xml',
          identity: 'Plant',
          kind: 'Changed',
          masterFingerprint: '1234567890',
          tiaFingerprint: 'abcdef0123',
          supported: true,
          evidenceKind: 'tag-table',
        },
      ],
    }))

    const { host } = await render({ signal: 1 })

    expect(host.textContent).toContain('Changed: Code')
    expect(host.textContent).not.toContain('Changed: Comments')
    expect(host.textContent).toContain('Content hash: 1234567 → abcdef0')
  })

  it('asks before retrying a missing-checksum comparison with automatic compile and save', async () => {
    const compare = vi.spyOn(api, 'compareMasterWithTia')
      .mockRejectedValueOnce(new api.WorkbenchApiError(400, 'PLC_CHECKSUM_UNAVAILABLE', "TIA did not provide a compiled software checksum for PLC 'PLC_1'."))
      .mockResolvedValueOnce(comparison())
    const { host } = await render({ signal: 1 })

    expect(host.textContent).toContain('Compile and save')
    expect(host.querySelector('[aria-label="Compile and save in TIA, then compare"]')).toBeTruthy()

    await click(host.querySelector('[aria-label="Compile and save in TIA, then compare"]')!)

    expect(compare).toHaveBeenNthCalledWith(2, 'wb-1', undefined, true)
    expect(host.textContent).toContain('PLC_1 · Main')
    expect(host.textContent).not.toContain('TIA differs from master')
  })

  it('asks before retrying when fingerprint capture reports an uncompiled PLC', async () => {
    const compare = vi.spyOn(api, 'compareMasterWithTia')
      .mockRejectedValueOnce(new api.WorkbenchApiError(400, 'PLC_NOT_COMPILED', "PLC 'PLC_1' has no readable software checksum. Compile it before capturing source evidence."))
      .mockResolvedValueOnce(comparison())
    const { host } = await render({ signal: 1 })

    expect(host.textContent).toContain('Compile and save')
    expect(host.querySelector('[aria-label="Compile and save in TIA, then compare"]')).toBeTruthy()

    await click(host.querySelector('[aria-label="Compile and save in TIA, then compare"]')!)

    expect(compare).toHaveBeenNthCalledWith(2, 'wb-1', undefined, true)
  })

  it('does not offer a per-selection push-to-TIA action', async () => {
    vi.spyOn(api, 'compareMasterWithTia').mockResolvedValue(comparison())
    const { host } = await render({ signal: 1, commitMessage: 'Accept Main' })

    await click(host.querySelector('input[type="checkbox"]')!)

    expect(host.querySelector('[aria-label="Push selected local changes to TIA"]')).toBeNull()
  })

  it('reports checked TIA paths to the global commit flow without an individual accept button', async () => {
    vi.spyOn(api, 'compareMasterWithTia').mockResolvedValue(comparison())
    const onSelectionChanged = vi.fn()
    const { host } = await render({ signal: 1, onSelectionChanged })

    await click(host.querySelector('input[type="checkbox"]')!)

    expect(onSelectionChanged).toHaveBeenLastCalledWith(
      'comparison-1',
      ['devices/PLC_1/source/Blocks/Main.xml'],
    )
    expect(host.querySelector('[aria-label="Accept selected TIA changes"]')).toBeNull()
    expect(host.textContent).not.toContain('Accept 1 into local repo')
  })


  it('reports the full compare as a tracked operation when the host supports it', async () => {
    const compare = vi.spyOn(api, 'compareMasterWithTia').mockResolvedValue(comparison({ differences: [], state: 'Consistent' }))
    const onBeginOperation = vi.fn(() => 'op-42')
    await render({ signal: 1, onBeginOperation })

    expect(onBeginOperation).toHaveBeenCalledWith('compare-tia', expect.stringContaining('Comparing'))
    expect(compare).toHaveBeenCalledWith('wb-1', 'op-42')
  })

  it('keeps a feature worktree project active for the master comparison', async () => {
    const compare = vi.spyOn(api, 'compareMasterWithTia').mockResolvedValue(comparison())
    await render({ branch: 'feature-a' })

    expect(compare).toHaveBeenCalledWith('wb-1', undefined, false, true, 'wt-1')
  })

  it('clears a committed comparison without running another TIA export', async () => {
    const compare = vi.spyOn(api, 'compareMasterWithTia').mockResolvedValue(comparison())
    const { host, root } = await render({ signal: 1 })
    expect(host.querySelector('[data-testid="vc-compare-result"]')).toBeTruthy()

    await act(async () => root.render(
      <VersionControlCompare
        workbenchId="wb-1"
        worktreeId="wt-1"
        branch="master"
        signal={1}
        commitMessage=""
        selectionResetSignal={1}
      />,
    ))

    expect(compare).toHaveBeenCalledTimes(1)
    expect(host.querySelector('[data-testid="vc-compare-result"]')).toBeNull()
  })

  it('shows the clean state when only the TIA checksum drifts', async () => {
    vi.spyOn(api, 'compareMasterWithTia').mockResolvedValue(comparison({
      differences: [],
      state: 'Different',
      liveChecksums: { 'dev-1': 'checksum-9' },
    }))
    const { host } = await render({ signal: 1 })

    expect(host.querySelector('[data-testid="vc-clean-state"]')?.textContent).toContain('TIA matches master')
  })

  it('reports TIA matches master when checksums match and no differences exist', async () => {
    vi.spyOn(api, 'compareMasterWithTia').mockResolvedValue(comparison({
      differences: [],
      state: 'Consistent',
      liveChecksums: { 'dev-1': 'checksum-2' },
    }))
    const { host } = await render({ signal: 1 })

    const cleanState = host.querySelector('[data-testid="vc-clean-state"]')!
    expect(cleanState.textContent).toContain('TIA matches master')
    expect(cleanState.textContent).toContain('Untrackable change')
  })

  it('reports partial coverage when hardware verification was skipped', async () => {
    const compare = vi.spyOn(api, 'compareMasterWithTia').mockResolvedValue(comparison({
      differences: [],
      state: 'Consistent',
      hardware: null,
      hardwareChecked: false,
    }))
    const { host } = await render({ verifyHardware: false })

    expect(compare).toHaveBeenCalledWith('wb-1', undefined, false, false)
    const cleanState = host.querySelector('[data-testid="vc-clean-state"]')!
    expect(cleanState.textContent).toContain('Managed source and safety match master')
    expect(cleanState.textContent).toContain('Hardware configuration was not checked.')
    expect(cleanState.textContent).not.toContain('TIA matches master')
    expect(host.querySelector('[aria-label="Accept TIA hardware configuration"]')).toBeNull()
  })

  it('accepts project hardware changes with the commit message as title', async () => {
    const hardware = {
      state: 'changed' as const,
      rootPath: 'C:/wb/worktrees/master/hardware',
      stagingPath: 'C:/wb/worktrees/master/hardware/staging',
      artifacts: [{ scope: 'project' as const, deviceName: null, state: 'changed' as const }],
      message: 'Project hardware configuration differs from TIA.',
    }
    vi.spyOn(api, 'compareMasterWithTia')
      .mockResolvedValueOnce(comparison({ differences: [], state: 'Different', hardware }))
      .mockResolvedValueOnce(comparison({ differences: [], state: 'Consistent', hardware: { ...hardware, state: 'in-sync', artifacts: [{ ...hardware.artifacts[0], state: 'same' }] } }))
    const overwrite = vi.spyOn(api, 'overwriteHardwareConfiguration').mockResolvedValue({
      rootPath: hardware.rootPath,
      artifactCount: 1,
      commitSha: 'hardware-commit',
    })
    const { host } = await render({ signal: 1, commitMessage: 'Add safety relay' })

    expect(host.textContent).toContain('Project hardware differs from TIA')
    await click(host.querySelector('button[aria-label="Accept TIA hardware configuration"]')!)

    expect(overwrite).toHaveBeenCalledWith('wb-1', 'wt-1', true, undefined, 'Add safety relay')
    expect(api.compareMasterWithTia).toHaveBeenCalledTimes(1)
  })

  it('lists changed F-blocks when the safety signature moved', async () => {
    vi.spyOn(api, 'compareMasterWithTia').mockResolvedValue(comparison({
      differences: [],
      state: 'Different',
      safetyChanged: true,
      safety: [{
        deviceId: 'dev-1',
        plcName: 'PLC_1',
        isSafetyDevice: true,
        readState: 'ok',
        fSignature: 'new-fold',
        baselineFSignature: 'old-fold',
        changed: true,
        changedBlocks: ['Program blocks/F_Main [FB1]', 'Program blocks/FOB_SAFETY [OB321]'],
      }],
    }))
    const { host } = await render({ signal: 1 })

    const card = host.querySelector('[data-testid="vc-safety-diff"]')
    expect(card).toBeTruthy()
    expect(card!.textContent).toContain('Safety program changed')
    expect(card!.textContent).toContain('PLC_1')
    expect(card!.textContent).toContain('Program blocks/F_Main [FB1]')
    expect(card!.textContent).toContain('Program blocks/FOB_SAFETY [OB321]')
    // With a safety-only change the headline must not claim a full match.
    expect(host.querySelector('[data-testid="vc-clean-state"]')?.textContent)
      .toContain('Tracked PLC source matches master')
  })

  it('notes when block-level safety detail is unavailable', async () => {
    vi.spyOn(api, 'compareMasterWithTia').mockResolvedValue(comparison({
      differences: [],
      state: 'Different',
      safetyChanged: true,
      safety: [{
        deviceId: 'dev-1',
        plcName: 'PLC_1',
        isSafetyDevice: true,
        readState: 'ok',
        fSignature: 'new-fold',
        baselineFSignature: 'old-fold',
        changed: true,
        changedBlocks: null,
      }],
    }))
    const { host } = await render({ signal: 1 })

    expect(host.querySelector('[data-testid="vc-safety-diff"]')?.textContent)
      .toContain('Block-level detail unavailable')
  })

})

const taskComparison = (overrides: Partial<api.TaskSourceComparison> = {}): api.TaskSourceComparison => ({
  taskId: 'task-1',
  deviceId: 'dev-1',
  candidates: [],
  candidateExports: [],
  problems: [],
  observedSoftwareChecksum: null,
  ...overrides,
})

describe('VersionControlCompare (task scope)', () => {
  it('compares only the active task through the task route and never the project scan', async () => {
    const projectCompare = vi.spyOn(api, 'compareMasterWithTia')
    const taskCompare = vi.spyOn(api, 'compareTaskWithTia').mockResolvedValue(taskComparison())
    const onSelectionChanged = vi.fn()
    const { host } = await render({ signal: 1, mode: 'task', taskId: 'task-1', taskTitle: 'Fix Main', onSelectionChanged })

    expect(taskCompare).toHaveBeenCalledWith('wb-1', 'wt-1', 'task-1', undefined)
    expect(projectCompare).not.toHaveBeenCalled()
    expect(host.querySelector('[data-testid="vc-task-compare-heading"]')?.textContent).toContain('Task compare: Fix Main')
    // A task-clean result clears the project selection instead of standing in for a project verdict.
    expect(onSelectionChanged).toHaveBeenLastCalledWith(null, [])
  })

  it('labels a clean task result task-clean without any project-clean wording', async () => {
    vi.spyOn(api, 'compareMasterWithTia')
    vi.spyOn(api, 'compareTaskWithTia').mockResolvedValue(taskComparison())
    const { host } = await render({ signal: 1, mode: 'task', taskId: 'task-1' })

    const taskResult = host.querySelector('[data-testid="vc-task-compare-result"]')!
    expect(taskResult.textContent).toContain('This task is in sync')
    expect(taskResult.textContent?.toLowerCase()).not.toContain('project')
    expect(taskResult.textContent?.toLowerCase()).not.toContain('master')
    expect(taskResult.textContent?.toLowerCase()).not.toContain('savepoint')
    expect(taskResult.textContent).not.toContain('All files committed')
    // Neither the project clean hero nor a project-only affordance can appear for a task result.
    expect(host.querySelector('[data-testid="vc-clean-state"]')).toBeNull()
    expect(taskResult.textContent).not.toContain('Untrackable change')
    expect(host.textContent).not.toContain('Prepare feature import')
    expect(api.compareMasterWithTia).not.toHaveBeenCalled()
  })

  it('explains a staged object with no committed baseline', async () => {
    vi.spyOn(api, 'compareTaskWithTia').mockResolvedValue(taskComparison({
      problems: [{
        sourceObjectId: 'dev-1:Main',
        code: 'TASK_STAGE_BASELINE_MISSING',
        message: 'This staged object has no fingerprint baseline yet because it has no committed Git content.',
      }],
    }))
    const { host } = await render({ signal: 1, mode: 'task', taskId: 'task-1' })

    const problem = host.querySelector('[data-testid="vc-task-problem"][data-problem-code="TASK_STAGE_BASELINE_MISSING"]')
    expect(problem?.textContent).toContain('No committed baseline yet')
    expect(problem?.textContent).toContain('Commit the object once')
    expect(problem?.textContent).toContain('dev-1:Main')
    expect(host.querySelector('[data-testid="vc-task-clean-state"]')).toBeNull()
  })

  it('explains an unreadable committed baseline', async () => {
    vi.spyOn(api, 'compareTaskWithTia').mockResolvedValue(taskComparison({
      problems: [{ sourceObjectId: 'dev-1:Main', code: 'TASK_STAGE_BASELINE_INVALID', message: 'baseline invalid' }],
    }))
    const { host } = await render({ signal: 1, mode: 'task', taskId: 'task-1' })

    expect(host.querySelector('[data-testid="vc-task-problem"][data-problem-code="TASK_STAGE_BASELINE_INVALID"]')?.textContent)
      .toContain('Committed baseline cannot be read')
    expect(host.querySelector('[data-testid="vc-task-clean-state"]')).toBeNull()
  })

  it('explains an object that is gone or unreadable in TIA', async () => {
    vi.spyOn(api, 'compareTaskWithTia').mockResolvedValue(taskComparison({
      problems: [{ sourceObjectId: 'dev-1:Main', code: 'TASK_STAGE_MISSING', message: 'missing' }],
    }))
    const { host } = await render({ signal: 1, mode: 'task', taskId: 'task-1' })

    const problem = host.querySelector('[data-testid="vc-task-problem"][data-problem-code="TASK_STAGE_MISSING"]')
    expect(problem?.textContent).toContain('Missing or unreadable in TIA')
    expect(problem?.textContent).toContain('gone, renamed, or unreadable in TIA')
    expect(host.querySelector('[data-testid="vc-task-clean-state"]')).toBeNull()
  })

  it('points at adding source objects when the task has no staged objects', async () => {
    vi.spyOn(api, 'compareTaskWithTia').mockRejectedValue(new api.WorkbenchApiError(
      400,
      'TASK_STAGE_EMPTY',
      'Add at least one source object to the task before comparing it with TIA.',
    ))
    const { host } = await render({ signal: 1, mode: 'task', taskId: 'task-1' })

    const problem = host.querySelector('[data-testid="vc-task-problem"][data-problem-code="TASK_STAGE_EMPTY"]')
    expect(problem?.textContent).toContain('No source objects are staged on this task')
    expect(problem?.textContent).toContain('Add source objects to the task before comparing it with TIA.')
    expect(host.querySelector('[data-testid="vc-task-clean-state"]')).toBeNull()
  })

  it('lists only the staged candidates and their XML exports', async () => {
    vi.spyOn(api, 'compareTaskWithTia').mockResolvedValue(taskComparison({
      candidates: [{ id: 'Main', reason: 'fingerprint-changed', requiresXmlExport: true, isSafetyDifference: false }],
      candidateExports: [{
        id: 'Main',
        sourcePath: 'devices/PLC_1/source/Blocks/Main.xml',
        export: { success: true, path: 'C:/staging/Main.xml' },
      }],
    }))
    const { host } = await render({ signal: 1, mode: 'task', taskId: 'task-1' })

    expect(host.querySelector('[data-testid="vc-task-candidate"]')?.textContent).toContain('Fingerprint changed')
    expect(host.querySelector('[data-testid="vc-task-candidate"]')?.textContent).toContain('devices/PLC_1/source/Blocks/Main.xml')
    expect(host.querySelector('[data-testid="vc-task-candidate-exports"]')?.textContent).toContain('Exported 1 of 1')
    expect(host.querySelector('[data-testid="vc-clean-state"]')).toBeNull()
  })

  it('runs no comparison when a task scope has no task to compare', async () => {
    const projectCompare = vi.spyOn(api, 'compareMasterWithTia')
    const taskCompare = vi.spyOn(api, 'compareTaskWithTia')
    const { host } = await render({ signal: 1, mode: 'task', taskId: null })

    expect(taskCompare).not.toHaveBeenCalled()
    expect(projectCompare).not.toHaveBeenCalled()
    expect(host.querySelector('[data-testid="vc-task-compare-result"]')).toBeNull()
  })

  it('drops a task result when the covered task changes', async () => {
    const taskCompare = vi.spyOn(api, 'compareTaskWithTia').mockResolvedValue(taskComparison())
    const { host, root } = await render({ signal: 1, mode: 'task', taskId: 'task-1' })
    expect(host.querySelector('[data-testid="vc-task-clean-state"]')).toBeTruthy()

    await act(async () => root.render(
      <VersionControlCompare
        workbenchId="wb-1"
        worktreeId="wt-1"
        branch="master"
        signal={1}
        mode="task"
        taskId="task-2"
        commitMessage=""
      />,
    ))

    expect(taskCompare).toHaveBeenCalledTimes(1)
    expect(host.querySelector('[data-testid="vc-task-compare-result"]')).toBeNull()
  })

  it('keeps a full-scan result when a task is opened in the same worktree', async () => {
    const projectCompare = vi.spyOn(api, 'compareMasterWithTia').mockResolvedValue(comparison({ differences: [], state: 'Consistent' }))
    const { host, root } = await render({ signal: 1 })

    expect(host.querySelector('[data-testid="vc-clean-state"]')).toBeTruthy()

    await act(async () => root.render(
      <VersionControlCompare
        workbenchId="wb-1"
        worktreeId="wt-1"
        branch="master"
        signal={1}
        taskId="task-1"
        commitMessage=""
      />,
    ))

    expect(projectCompare).toHaveBeenCalledTimes(1)
    expect(host.querySelector('[data-testid="vc-clean-state"]')?.textContent).toContain('TIA matches master')
  })
})
