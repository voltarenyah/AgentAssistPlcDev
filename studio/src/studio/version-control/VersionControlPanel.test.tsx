// @vitest-environment happy-dom
import React, { act } from 'react'
import { createRoot } from 'react-dom/client'
import { afterEach, describe, expect, it, vi } from 'vitest'
import * as api from '@/api/client'
import VersionControlPanel from './VersionControlPanel'

globalThis.IS_REACT_ACT_ENVIRONMENT = true

const mockVcState = (overrides: {
  entries?: api.VcStatusEntry[]
  commits?: api.VcCommitEntry[]
  timeline?: api.VersionControlTimelineResult
  savepoints?: api.SavepointInfo[]
  activeTask?: api.EngineeringTask | null
  commitTasks?: api.EngineeringTask[]
} = {}) => {
  const status = vi.spyOn(api, 'getWorktreeVcStatus').mockResolvedValue({
    repoPath: 'C:/repos/demo',
    branch: 'feature-a',
    entries: overrides.entries ?? [],
  })
  const log = vi.spyOn(api, 'getWorktreeVcLog').mockResolvedValue({
    repoPath: 'C:/repos/demo',
    commits: overrides.commits ?? [],
  })
  const timeline = vi.spyOn(api, 'getWorktreeVersionControlTimeline').mockResolvedValue(overrides.timeline ?? {
    gitCommits: [],
    svnRevisions: [],
    hasMore: false,
  })
  const savepoints = vi.spyOn(api, 'getWorktreeSavepoints').mockResolvedValue(overrides.savepoints ?? [])
  const activeTask = vi.spyOn(api, 'getActiveWorktreeTask').mockResolvedValue({ activeTask: overrides.activeTask ?? null })
  vi.spyOn(api, 'listGraphWorktreeTasks').mockResolvedValue(overrides.commitTasks ?? [])
  vi.spyOn(api, 'setActiveWorktreeTask').mockResolvedValue({ activeTask: null })
  return { status, log, timeline, savepoints, activeTask }
}

const worktreeTask = (overrides: Partial<api.EngineeringTask> = {}): api.EngineeringTask => ({
  taskId: 'task-1',
  workbenchId: 'wb-1',
  scope: 'worktree',
  worktreeId: 'wt-1',
  title: 'Fix Main',
  type: 'feature',
  status: 'todo',
  priority: 0,
  intent: '',
  expectedResult: '',
  description: null,
  createdUtc: '2026-09-30T08:00:00.000Z',
  updatedUtc: '2026-09-30T08:00:00.000Z',
  deviceId: 'dev-1',
  ...overrides,
})

const render = async (element: React.ReactNode) => {
  const host = document.createElement('div')
  document.body.appendChild(host)
  const root = createRoot(host)
  await act(async () => root.render(element))
  return { host, root }
}

const rerender = async (root: ReturnType<typeof createRoot>, element: React.ReactNode) => {
  await act(async () => root.render(element))
}

const click = async (element: Element) => {
  await act(async () => {
    element.dispatchEvent(new MouseEvent('click', { bubbles: true }))
  })
}

afterEach(() => {
  vi.restoreAllMocks()
  document.body.innerHTML = ''
})

describe('VersionControlPanel (right dock page content)', () => {
  it('renders the changes half with its compare action and branch block, and no page tab strip', async () => {
    mockVcState()
    const { host } = await render(<VersionControlPanel workbenchId="wb-1" worktreeId="wt-1" section="changes" />)

    // The rail owns the page switch now; the panel no longer renders its own tab bar.
    expect(host.querySelector('[data-testid="vc-tab-changes"]')).toBeNull()
    expect(host.querySelector('[data-testid="vc-tab-history"]')).toBeNull()
    expect(host.querySelector('nav[aria-label="Version control sections"]')).toBeNull()

    expect(host.querySelector('[data-testid="version-control-changes"]')).toBeTruthy()
    expect(host.querySelector('[data-testid="vc-compare-open"]')?.textContent).toContain('Compare')
    expect(host.querySelector('[data-testid="vc-compare-open"]')?.textContent).not.toContain('Compare with TIA')
    expect(host.querySelector('[data-testid="vc-branch-name"]')?.textContent).toBe('feature-a')
    expect(host.textContent).toContain('master')
  })

  it('renders the compare controls in the changes half and none of them in the history half', async () => {
    mockVcState()
    const changes = await render(<VersionControlPanel workbenchId="wb-1" worktreeId="wt-1" section="changes" />)

    expect(changes.host.querySelector('[data-testid="vc-compare-open"]')).toBeTruthy()
    expect(changes.host.querySelector('[data-testid="vc-compare-mode"]')).toBeTruthy()
    expect(changes.host.querySelector('[data-testid="vc-compare-mode-full"]')).toBeTruthy()
    expect(changes.host.querySelector('[data-testid="vc-compare-mode-task"]')).toBeTruthy()
    expect(changes.host.querySelector('[data-testid="vc-verify-hardware"]')).toBeTruthy()

    const history = await render(<VersionControlPanel workbenchId="wb-1" worktreeId="wt-1" section="history" />)

    expect(history.host.querySelector('[data-testid="version-control-history"]')).toBeTruthy()
    expect(history.host.querySelector('[data-testid="vc-compare-open"]')).toBeNull()
    expect(history.host.querySelector('[data-testid="vc-compare-mode"]')).toBeNull()
    expect(history.host.querySelector('[data-testid="vc-compare-mode-full"]')).toBeNull()
    expect(history.host.querySelector('[data-testid="vc-compare-mode-task"]')).toBeNull()
    expect(history.host.querySelector('[data-testid="vc-verify-hardware"]')).toBeNull()
    expect(history.host.querySelector('[data-testid="vc-compare-task-unavailable"]')).toBeNull()
  })

  it('reads only the data the history half shows', async () => {
    const { status, log, timeline, savepoints, activeTask } = mockVcState()
    const onCount = vi.fn()
    await render(<VersionControlPanel workbenchId="wb-1" worktreeId="wt-1" section="history" onUncommittedCountChange={onCount} />)

    expect(log).toHaveBeenCalledTimes(1)
    expect(timeline).toHaveBeenCalledTimes(1)
    expect(savepoints).toHaveBeenCalledTimes(1)
    expect(status).not.toHaveBeenCalled()
    // The task scope decides a comparison, which the history half never runs.
    expect(activeTask).not.toHaveBeenCalled()
    // The rail badge belongs to the changes half; the history instance never clears it.
    expect(onCount).not.toHaveBeenCalled()
  })

  it('reads the worktree status and the timeline markers the changes half shows', async () => {
    const { status, log, timeline } = mockVcState()
    await render(<VersionControlPanel workbenchId="wb-1" worktreeId="wt-1" section="changes" />)

    expect(status).toHaveBeenCalledTimes(1)
    expect(log).toHaveBeenCalledTimes(1)
    // The savepoint warning's untrackable flag exists only on the timeline route.
    expect(timeline).toHaveBeenCalledTimes(1)
  })

  it('shows the clean-state hero and the snapshot area on the changes page', async () => {
    mockVcState({
      savepoints: [{ sha: 'abc', message: 'snap', svnUrl: null, svnRevision: 3, projectChecksum: null, compileStatus: null, fSignature: null }],
    })
    const { host } = await render(<VersionControlPanel workbenchId="wb-1" worktreeId="wt-1" section="changes" />)

    expect(host.querySelector('[data-testid="vc-changes-empty"]')?.textContent).toContain('No changes on this branch')
    expect(host.querySelector('[data-testid="vc-snapshot-revision"]')?.textContent).toBe('r3')
  })

  it('counts commits after the latest SVN revision boundary', async () => {
    const commit = (sha: string): api.VcCommitEntry => ({
      sha,
      author: 'PLC Assistant',
      message: sha,
      timestamp: '2026-08-23T08:00:00.000Z',
      files: [],
      validationState: 'Unlabeled',
    })
    const savepoint = (sha: string, revision: number): api.SavepointInfo => ({
      sha,
      message: sha,
      svnUrl: '^/native/main',
      svnRevision: revision,
      projectChecksum: null,
      compileStatus: 'SUCCESS',
      fSignature: null,
    })
    mockVcState({
      commits: [commit('new-3'), commit('new-2'), commit('new-1'), commit('snapshot-r3'), commit('old-r2')],
      savepoints: [savepoint('new-3', 3), savepoint('new-2', 3), savepoint('new-1', 3), savepoint('snapshot-r3', 3), savepoint('old-r2', 2)],
    })
    const { host } = await render(<VersionControlPanel workbenchId="wb-1" worktreeId="wt-1" section="changes" />)

    expect(host.querySelector('[data-testid="vc-snapshot-drift"]')?.textContent).toContain('3 commits since')
  })

  it('renders the history timeline and opens a commit detail', async () => {
    mockVcState({
      savepoints: [{ sha: 'abcdef1234567890', message: 'native savepoint', svnUrl: '^/native/main', svnRevision: 4, projectChecksum: 'PLC_1:AA BB', compileStatus: 'SUCCESS', fSignature: null }],
      timeline: {
        gitCommits: [{
          sha: 'abcdef1234567890',
          author: 'Ansel',
          message: 'Validate Main block',
          timestamp: '2026-08-04T08:00:00.000Z',
          files: ['devices/PLC_1/source/Blocks/Main.xml'],
          tiaChecksum: null,
          svnRevision: null,
        }],
        svnRevisions: [{
          revision: 4,
          author: 'PLC Assistant',
          message: 'before IP change',
          timestamp: '2026-08-04T08:01:00.000Z',
          tiaChecksum: null,
          gitCommitSha: 'abcdef1234567890',
        }],
        hasMore: false,
      },
    })
    const { host } = await render(<VersionControlPanel workbenchId="wb-1" worktreeId="wt-1" section="history" />)

    expect(host.querySelector('[data-testid="commit-abcdef1"]')).toBeTruthy()
    expect(host.querySelector('[data-testid="savepoint-r4"]')).toBeTruthy()

    await click(host.querySelector('[data-testid="commit-abcdef1"]')!)
    expect(host.querySelector('[data-testid="timeline-detail"]')?.textContent).not.toContain('PLC_1:AA BB')
  })

  it('executes the TIA comparison directly from the changes action', async () => {
    mockVcState()
    const compare = vi.spyOn(api, 'compareMasterWithTia').mockResolvedValue({
      comparisonId: 'comparison-1',
      masterSha: 'master-1',
      fastGatePassed: true,
      state: 'Consistent',
      liveChecksums: {},
      differences: [],
    })
    vi.spyOn(api, 'getWorktreeEngineeringState').mockRejectedValue(new Error('no state'))
    const { host } = await render(<VersionControlPanel workbenchId="wb-1" worktreeId="wt-1" section="changes" />)

    await click(host.querySelector('[data-testid="vc-compare-open"]')!)

    expect(compare).toHaveBeenCalledTimes(1)
    // A clean comparison now shows the clean-state block instead of a blank area.
    expect(host.querySelector('[data-testid="vc-clean-state"]')?.textContent).toContain('TIA matches master')
    expect(host.querySelector('[data-testid="vc-changes-empty"]')).toBeNull()
  })

  it('checks hardware verification by default and forwards an opt-out to the comparison', async () => {
    mockVcState()
    const compare = vi.spyOn(api, 'compareMasterWithTia').mockResolvedValue({
      comparisonId: 'comparison-1',
      masterSha: 'master-1',
      fastGatePassed: true,
      state: 'Consistent',
      liveChecksums: {},
      differences: [],
      hardwareChecked: false,
    })
    vi.spyOn(api, 'getWorktreeEngineeringState').mockRejectedValue(new Error('no state'))
    const { host } = await render(<VersionControlPanel workbenchId="wb-1" worktreeId="wt-1" section="changes" />)

    const checkbox = host.querySelector<HTMLInputElement>('[data-testid="vc-verify-hardware"]')!
    expect(checkbox.checked).toBe(true)
    await click(checkbox)
    expect(checkbox.checked).toBe(false)
    await click(host.querySelector('[data-testid="vc-compare-open"]')!)

    expect(compare).toHaveBeenCalledWith('wb-1', undefined, false, false, 'wt-1')
  })

  it('does not describe the branch as clean when TIA comparison finds differences', async () => {
    mockVcState()
    vi.spyOn(api, 'compareMasterWithTia').mockResolvedValue({
      comparisonId: 'comparison-1',
      masterSha: 'master-1',
      fastGatePassed: false,
      state: 'Different',
      liveChecksums: { 'dev-1': 'checksum-2' },
      differences: [{
        deviceId: 'dev-1', plcName: 'PLC_1', relativePath: 'devices/PLC_1/source/Blocks/Main.xml',
        identity: 'Main', kind: 'Changed', masterFingerprint: 'old', tiaFingerprint: 'new', supported: true,
      }],
    })
    vi.spyOn(api, 'getWorktreeEngineeringState').mockRejectedValue(new Error('no state'))
    const { host } = await render(<VersionControlPanel workbenchId="wb-1" worktreeId="wt-1" section="changes" />)

    await click(host.querySelector('[data-testid="vc-compare-open"]')!)

    expect(host.querySelector('[data-testid="vc-changes-empty"]')).toBeNull()
    expect(host.textContent).toContain('PLC_1 · Main')
    expect(host.textContent).not.toContain('TIA differs from master')
  })

  it('keeps the comparison result when the history page mounts beside it', async () => {
    mockVcState()
    const compare = vi.spyOn(api, 'compareMasterWithTia').mockResolvedValue({
      comparisonId: 'comparison-1',
      masterSha: 'master-1',
      fastGatePassed: true,
      state: 'Consistent',
      liveChecksums: {},
      differences: [],
    })
    vi.spyOn(api, 'getWorktreeEngineeringState').mockRejectedValue(new Error('no state'))
    const { host } = await render(
      <>
        <div data-testid="changes-page"><VersionControlPanel workbenchId="wb-1" worktreeId="wt-1" section="changes" /></div>
        <div data-testid="history-page"><VersionControlPanel workbenchId="wb-1" worktreeId="wt-1" section="history" /></div>
      </>,
    )

    await click(host.querySelector('[data-testid="changes-page"] [data-testid="vc-compare-open"]')!)
    expect(compare).toHaveBeenCalledTimes(1)

    // The other page keeps its own mounted surface without re-running the changes comparison.
    expect(host.querySelector('[data-testid="history-page"] [data-testid="version-control-history"]')).toBeTruthy()
    expect(host.querySelector('[data-testid="changes-page"] [data-testid="vc-clean-state"]')?.textContent).toContain('TIA matches master')
    expect(compare).toHaveBeenCalledTimes(1)
  })

  const logCommit = (sha: string): api.VcCommitEntry => ({
    sha,
    author: 'PLC Assistant',
    message: sha,
    timestamp: '2026-08-23T08:00:00.000Z',
    files: [],
    validationState: 'Unlabeled',
    evidenceKind: null,
  })
  const timelineCommit = (sha: string, untrackableChange: boolean | null): api.VersionControlTimelineGitCommit => ({
    sha,
    author: 'PLC Assistant',
    message: sha,
    timestamp: '2026-08-23T08:00:00.000Z',
    files: [],
    tiaChecksum: null,
    svnRevision: null,
    untrackableChange,
  })
  const savepointAt = (sha: string, revision: number): api.SavepointInfo => ({
    sha,
    message: sha,
    svnUrl: '^/native/main',
    svnRevision: revision,
    projectChecksum: null,
    compileStatus: 'SUCCESS',
    fSignature: null,
  })

  it('maps the untrackable-change flag onto the history timeline', async () => {
    mockVcState({
      commits: [logCommit('untrackable-1')],
      timeline: {
        gitCommits: [timelineCommit('untrackable-1', true)],
        svnRevisions: [],
        hasMore: false,
      },
    })
    const { host } = await render(<VersionControlPanel workbenchId="wb-1" worktreeId="wt-1" section="history" />)

    expect(host.querySelector('[data-testid="vc-untrackable-marker"]')?.textContent).toContain('untrackable')
  })

  it('maps the savepoint safety fields onto the history timeline', async () => {
    mockVcState({
      savepoints: [{ ...savepointAt('safety-1', 5), safetyChanged: true, safetyReadState: 'read-failed' }],
      timeline: {
        gitCommits: [],
        svnRevisions: [{
          revision: 5,
          author: 'PLC Assistant',
          message: 'safety-1',
          timestamp: '2026-08-23T08:00:00.000Z',
          tiaChecksum: null,
          gitCommitSha: 'safety-1',
        }],
        hasMore: false,
      },
    })
    const { host } = await render(<VersionControlPanel workbenchId="wb-1" worktreeId="wt-1" section="history" />)

    expect(host.querySelector('[data-testid="vc-safety-change-marker"]')?.textContent).toContain('Safety change')
    expect(host.querySelector('[data-testid="vc-safety-unavailable-marker"]')?.textContent).toContain('Safety signature unavailable')
  })

  it('renders no safety badges for a savepoint without safety findings', async () => {
    mockVcState({
      savepoints: [{ ...savepointAt('plain-1', 5), safetyReadState: 'ok' }],
      timeline: {
        gitCommits: [],
        svnRevisions: [{
          revision: 5,
          author: 'PLC Assistant',
          message: 'plain-1',
          timestamp: '2026-08-23T08:00:00.000Z',
          tiaChecksum: null,
          gitCommitSha: 'plain-1',
        }],
        hasMore: false,
      },
    })
    const { host } = await render(<VersionControlPanel workbenchId="wb-1" worktreeId="wt-1" section="history" />)

    expect(host.querySelector('[data-testid="vc-safety-change-marker"]')).toBeNull()
    expect(host.querySelector('[data-testid="vc-safety-unavailable-marker"]')).toBeNull()
  })

  it('warns about a pending savepoint when an untrackable commit is newer than the savepoint boundary', async () => {
    mockVcState({
      commits: [logCommit('new-untrackable'), logCommit('snapshot-r3'), logCommit('old-r2')],
      savepoints: [savepointAt('snapshot-r3', 3), savepointAt('old-r2', 2)],
      timeline: {
        gitCommits: [timelineCommit('new-untrackable', true), timelineCommit('snapshot-r3', null), timelineCommit('old-r2', null)],
        svnRevisions: [],
        hasMore: false,
      },
    })
    const { host } = await render(<VersionControlPanel workbenchId="wb-1" worktreeId="wt-1" section="changes" />)

    expect(host.querySelector('[data-testid="vc-untrackable-savepoint-warning"]')).toBeTruthy()
  })

  it('stays quiet when the untrackable commit is older than the savepoint boundary', async () => {
    mockVcState({
      commits: [logCommit('snapshot-r3'), logCommit('old-untrackable')],
      savepoints: [savepointAt('snapshot-r3', 3)],
      timeline: {
        gitCommits: [timelineCommit('snapshot-r3', null), timelineCommit('old-untrackable', true)],
        svnRevisions: [],
        hasMore: false,
      },
    })
    const { host } = await render(<VersionControlPanel workbenchId="wb-1" worktreeId="wt-1" section="changes" />)

    expect(host.querySelector('[data-testid="vc-untrackable-savepoint-warning"]')).toBeNull()
  })

  it('warns about a pending savepoint when an untrackable commit exists and no savepoint exists at all', async () => {
    mockVcState({
      commits: [logCommit('new-untrackable')],
      timeline: {
        gitCommits: [timelineCommit('new-untrackable', true)],
        svnRevisions: [],
        hasMore: false,
      },
    })
    const { host } = await render(<VersionControlPanel workbenchId="wb-1" worktreeId="wt-1" section="changes" />)

    expect(host.querySelector('[data-testid="vc-untrackable-savepoint-warning"]')).toBeTruthy()
  })

  const taskStage = (sourceObjectId: string): api.TaskSourceStage => ({
    taskId: 'task-1',
    sourceObjectId,
    deviceId: 'dev-1',
    baselineEvidenceJson: '{}',
    stagedUtc: '2026-09-30T08:00:00.000Z',
  })

  const taskResult = (overrides: Partial<api.WorkbenchConsistencyResult> = {}): api.WorkbenchConsistencyResult => ({
    comparisonId: 'comparison-1',
    masterSha: 'master-1',
    fastGatePassed: false,
    state: 'Consistent',
    liveChecksums: {},
    differences: [],
    hardwareChecked: false,
    comparedTaskId: 'task-1',
    stageProblems: [],
    ...overrides,
  })

  it('offers Full scan and Task only, scoping to the active task by default', async () => {
    const { activeTask } = mockVcState({ activeTask: worktreeTask() })
    vi.spyOn(api, 'getWorktreeEngineeringState').mockRejectedValue(new Error('no state'))
    vi.spyOn(api, 'listTaskSourceStages').mockResolvedValue([taskStage('dev-1:Main')])
    const { host } = await render(<VersionControlPanel workbenchId="wb-1" worktreeId="wt-1" section="changes" />)

    expect(activeTask).toHaveBeenCalledWith('wb-1', 'wt-1')
    expect(host.querySelector('[data-testid="vc-compare-mode-full"]')?.textContent).toContain('Full scan')
    expect(host.querySelector('[data-testid="vc-compare-mode-task"]')?.textContent).toContain('Task only')
    // A task's staged objects are the quick in-scope comparison, so they are the default scope.
    expect(host.querySelector('[data-testid="vc-compare-mode-task"]')?.getAttribute('data-state')).toBe('on')
    expect(host.querySelector('[data-testid="vc-compare-mode-full"]')?.getAttribute('data-state')).toBe('off')
    // Hardware verification is project-wide and cannot apply to a task-only comparison.
    expect(host.querySelector<HTMLInputElement>('[data-testid="vc-verify-hardware"]')!.disabled).toBe(true)
  })

  it('falls back to the project-wide scan when no task can scope a comparison', async () => {
    mockVcState()
    const { host } = await render(<VersionControlPanel workbenchId="wb-1" worktreeId="wt-1" section="changes" />)

    expect(host.querySelector('[data-testid="vc-compare-mode-full"]')?.getAttribute('data-state')).toBe('on')
    expect(host.querySelector('[data-testid="vc-compare-mode-task"]')?.getAttribute('data-state')).toBe('off')
    expect(host.querySelector<HTMLInputElement>('[data-testid="vc-verify-hardware"]')!.disabled).toBe(false)
  })

  it('keeps Task only unavailable and explained when the worktree has no active task', async () => {
    const { activeTask } = mockVcState()
    const projectCompare = vi.spyOn(api, 'compareMasterWithTia')
    const taskCompare = vi.spyOn(api, 'compareTaskWithTia')
    const stages = vi.spyOn(api, 'listTaskSourceStages')
    const { host } = await render(<VersionControlPanel workbenchId="wb-1" worktreeId="wt-1" section="changes" />)

    await click(host.querySelector('button[aria-label="Task only"]')!)

    expect(activeTask).toHaveBeenCalledWith('wb-1', 'wt-1')
    expect(host.querySelector('[data-testid="vc-compare-task-unavailable"]')?.textContent)
      .toContain('Select a task for this worktree')
    expect(host.querySelector<HTMLButtonElement>('[data-testid="vc-compare-open"]')!.disabled).toBe(true)
    await click(host.querySelector('[data-testid="vc-compare-open"]')!)
    expect(taskCompare).not.toHaveBeenCalled()
    expect(projectCompare).not.toHaveBeenCalled()
    expect(stages).not.toHaveBeenCalled()
  })

  it('keeps Task only unavailable and explained while the active task has no staged objects', async () => {
    mockVcState({ activeTask: worktreeTask({ title: 'Empty task' }) })
    vi.spyOn(api, 'getWorktreeEngineeringState').mockRejectedValue(new Error('no state'))
    vi.spyOn(api, 'listTaskSourceStages').mockResolvedValue([])
    const taskCompare = vi.spyOn(api, 'compareTaskWithTia')
    const { host } = await render(<VersionControlPanel workbenchId="wb-1" worktreeId="wt-1" section="changes" />)

    await click(host.querySelector('button[aria-label="Task only"]')!)

    expect(host.querySelector('[data-testid="vc-compare-task-unavailable"]')?.textContent)
      .toContain('Add source objects to the task first')
    expect(host.querySelector<HTMLButtonElement>('[data-testid="vc-compare-open"]')!.disabled).toBe(true)
    expect(taskCompare).not.toHaveBeenCalled()
  })

  it('keeps Task only unavailable for an active task that cannot own stages', async () => {
    mockVcState({ activeTask: worktreeTask({ scope: 'project', worktreeId: null, title: 'Project task' }) })
    const stages = vi.spyOn(api, 'listTaskSourceStages')
    const { host } = await render(<VersionControlPanel workbenchId="wb-1" worktreeId="wt-1" section="changes" />)

    await click(host.querySelector('button[aria-label="Task only"]')!)

    expect(host.querySelector('[data-testid="vc-compare-task-unavailable"]')?.textContent)
      .toContain('The active task is not a task of this worktree')
    expect(stages).not.toHaveBeenCalled()
  })

  it('keeps Task only unavailable for an active task with no PLC binding', async () => {
    mockVcState({ activeTask: worktreeTask({ deviceId: null, title: 'Hardware task' }) })
    const stages = vi.spyOn(api, 'listTaskSourceStages')
    const { host } = await render(<VersionControlPanel workbenchId="wb-1" worktreeId="wt-1" section="changes" />)

    await click(host.querySelector('button[aria-label="Task only"]')!)

    expect(host.querySelector('[data-testid="vc-compare-task-unavailable"]')?.textContent)
      .toContain('not bound to a PLC device')
    expect(stages).not.toHaveBeenCalled()
  })

  it('runs Task only against the worktree active task route and never the project scan', async () => {
    mockVcState({ activeTask: worktreeTask() })
    vi.spyOn(api, 'getWorktreeEngineeringState').mockRejectedValue(new Error('no state'))
    vi.spyOn(api, 'listTaskSourceStages').mockResolvedValue([taskStage('dev-1:Main')])
    const projectCompare = vi.spyOn(api, 'compareMasterWithTia')
    const taskCompare = vi.spyOn(api, 'compareTaskWithTia').mockResolvedValue(taskResult())
    const { host } = await render(<VersionControlPanel workbenchId="wb-1" worktreeId="wt-1" section="changes" />)

    await click(host.querySelector('button[aria-label="Task only"]')!)
    await click(host.querySelector('[data-testid="vc-compare-open"]')!)

    expect(taskCompare).toHaveBeenCalledTimes(1)
    expect(taskCompare).toHaveBeenCalledWith('wb-1', 'wt-1', 'task-1', undefined)
    expect(projectCompare).not.toHaveBeenCalled()
    expect(host.querySelector('[data-testid="vc-compare-differences"]')).toBeTruthy()
    expect(host.querySelector('[data-testid="vc-task-clean-state"]')?.textContent).toContain('This task is in sync')
    expect(host.querySelector('[data-testid="vc-clean-state"]')).toBeNull()
  })

  it('reports task differences as selectable rows instead of a project-clean state', async () => {
    mockVcState({ activeTask: worktreeTask() })
    vi.spyOn(api, 'getWorktreeEngineeringState').mockRejectedValue(new Error('no state'))
    vi.spyOn(api, 'listTaskSourceStages').mockResolvedValue([taskStage('dev-1:Main')])
    vi.spyOn(api, 'compareTaskWithTia').mockResolvedValue(taskResult({
      state: 'Different',
      differences: [{
        deviceId: 'dev-1',
        plcName: 'PLC_1',
        relativePath: 'devices/PLC_1/source/Blocks/Main.xml',
        identity: 'Main',
        kind: 'Changed',
        masterFingerprint: 'old',
        tiaFingerprint: 'new',
        supported: true,
        fingerprintComponents: { Code: { stored: 'old', live: 'new', matches: false } },
      }],
    }))
    const { host } = await render(<VersionControlPanel workbenchId="wb-1" worktreeId="wt-1" section="changes" />)

    await click(host.querySelector('button[aria-label="Task only"]')!)
    await click(host.querySelector('[data-testid="vc-compare-open"]')!)

    expect(host.querySelector('[data-testid="vc-compare-differences"]')?.textContent).toContain('Changed: Code')
    expect(host.querySelector<HTMLInputElement>('[data-testid="vc-compare-differences"] input[type="checkbox"]')).toBeTruthy()
    expect(host.querySelector('[data-testid="vc-task-clean-state"]')).toBeNull()
    expect(host.querySelector('[data-testid="vc-clean-state"]')).toBeNull()
  })

  const vcEntry = (filePath: string): api.VcStatusEntry => ({ filePath, state: 'Modified', staged: false })

  it('offers the worktree’s device-bound tasks as commit targets, defaulting to the active task', async () => {
    mockVcState({
      entries: [vcEntry('devices/PLC_1/source/Blocks/Main.xml')],
      activeTask: worktreeTask(),
      commitTasks: [worktreeTask(), worktreeTask({ taskId: 'task-2', title: 'Tune drive' })],
    })
    const { host } = await render(<VersionControlPanel workbenchId="wb-1" worktreeId="wt-1" section="changes" />)
    await act(async () => {})

    const select = host.querySelector<HTMLSelectElement>('[data-testid="vc-commit-task"]')
    expect(select).not.toBeNull()
    expect(select!.value).toBe('task-1')
    expect(Array.from(select!.options).map(option => option.textContent)).toEqual(['Fix Main', 'Tune drive'])
  })

  it('makes the chosen commit target the worktree’s active task and re-reads the task context', async () => {
    const { activeTask } = mockVcState({
      entries: [vcEntry('devices/PLC_1/source/Blocks/Main.xml')],
      activeTask: worktreeTask(),
      commitTasks: [worktreeTask(), worktreeTask({ taskId: 'task-2', title: 'Tune drive' })],
    })
    const setActive = vi.spyOn(api, 'setActiveWorktreeTask')
      .mockResolvedValue({ activeTask: worktreeTask({ taskId: 'task-2', title: 'Tune drive' }) })
    const { host } = await render(<VersionControlPanel workbenchId="wb-1" worktreeId="wt-1" section="changes" />)
    await act(async () => {})

    const select = host.querySelector<HTMLSelectElement>('[data-testid="vc-commit-task"]')!
    await act(async () => {
      Object.getOwnPropertyDescriptor(window.HTMLSelectElement.prototype, 'value')!.set!.call(select, 'task-2')
      select.dispatchEvent(new Event('change', { bubbles: true }))
    })

    expect(setActive).toHaveBeenCalledWith('wb-1', 'wt-1', 'task-2')
    expect(activeTask).toHaveBeenCalledTimes(2)
  })

  it('reads the worktree active task on mount and re-reads it when the header refresh signal changes', async () => {
    const { activeTask } = mockVcState({ activeTask: worktreeTask() })
    vi.spyOn(api, 'getWorktreeEngineeringState').mockRejectedValue(new Error('no state'))
    vi.spyOn(api, 'listTaskSourceStages').mockResolvedValue([taskStage('dev-1:Main')])
    const { root } = await render(<VersionControlPanel workbenchId="wb-1" worktreeId="wt-1" section="changes" refreshSignal={0} />)

    // The default scope needs the active task, so it is read without waiting for a scope switch.
    expect(activeTask).toHaveBeenCalledTimes(1)

    await rerender(root, <VersionControlPanel workbenchId="wb-1" worktreeId="wt-1" section="changes" refreshSignal={1} />)

    expect(activeTask).toHaveBeenCalledTimes(2)
  })

  it('re-reads the changes data when the header refresh signal changes', async () => {
    const { status, log } = mockVcState()
    const { root } = await render(<VersionControlPanel workbenchId="wb-1" worktreeId="wt-1" section="changes" refreshSignal={0} />)

    expect(status).toHaveBeenCalledTimes(1)
    expect(log).toHaveBeenCalledTimes(1)

    await rerender(root, <VersionControlPanel workbenchId="wb-1" worktreeId="wt-1" section="changes" refreshSignal={1} />)

    expect(status).toHaveBeenCalledTimes(2)
    expect(log).toHaveBeenCalledTimes(2)
  })

  it('re-reads the history data when the header refresh signal changes', async () => {
    const { log, timeline } = mockVcState()
    const { root } = await render(<VersionControlPanel workbenchId="wb-1" worktreeId="wt-1" section="history" refreshSignal={0} />)

    expect(log).toHaveBeenCalledTimes(1)
    expect(timeline).toHaveBeenCalledTimes(1)

    await rerender(root, <VersionControlPanel workbenchId="wb-1" worktreeId="wt-1" section="history" refreshSignal={1} />)

    expect(log).toHaveBeenCalledTimes(2)
    expect(timeline).toHaveBeenCalledTimes(2)
  })

  it('reports the visible uncommitted object count, and zero when the branch is clean', async () => {
    mockVcState({
      entries: [
        vcEntry('devices/PLC_1/source/Blocks/Main.xml'),
        vcEntry('devices/PLC_1/source/Blocks/FB_Speed.xml'),
        // Not a source object, so it is not part of the count the rail badges.
        vcEntry('README.md'),
      ],
    })
    const onCount = vi.fn()
    await render(<VersionControlPanel workbenchId="wb-1" worktreeId="wt-1" section="changes" onUncommittedCountChange={onCount} />)

    expect(onCount).toHaveBeenLastCalledWith(2)

    mockVcState()
    const onCleanCount = vi.fn()
    await render(<VersionControlPanel workbenchId="wb-1" worktreeId="wt-1" section="changes" onUncommittedCountChange={onCleanCount} />)

    expect(onCleanCount).toHaveBeenLastCalledWith(0)
  })
})
