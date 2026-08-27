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
} = {}) => {
  vi.spyOn(api, 'getWorktreeVcStatus').mockResolvedValue({
    repoPath: 'C:/repos/demo',
    branch: 'feature-a',
    entries: overrides.entries ?? [],
  })
  vi.spyOn(api, 'getWorktreeVcLog').mockResolvedValue({
    repoPath: 'C:/repos/demo',
    commits: overrides.commits ?? [],
  })
  vi.spyOn(api, 'getWorktreeVersionControlTimeline').mockResolvedValue(overrides.timeline ?? {
    gitCommits: [],
    svnRevisions: [],
    hasMore: false,
  })
  vi.spyOn(api, 'getWorktreeSavepoints').mockResolvedValue(overrides.savepoints ?? [])
}

const render = async (element: React.ReactNode) => {
  const host = document.createElement('div')
  document.body.appendChild(host)
  const root = createRoot(host)
  await act(async () => root.render(element))
  return { host, root }
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

describe('VersionControlPanel (worktree dock)', () => {
  it('renders the two-page tab bar, compare action, and branch block', async () => {
    mockVcState()
    const { host } = await render(<VersionControlPanel workbenchId="wb-1" worktreeId="wt-1" />)

    expect(host.querySelector('[data-testid="vc-tab-changes"]')).toBeTruthy()
    expect(host.querySelector('[data-testid="vc-tab-history"]')).toBeTruthy()
    expect(host.querySelector('[data-testid="vc-compare-open"]')?.textContent).toContain('Compare with TIA')
    expect(host.querySelector('[data-testid="vc-branch-name"]')?.textContent).toBe('feature-a')
    expect(host.textContent).toContain('master')
  })

  it('shows the clean-state hero and the snapshot area on the changes page', async () => {
    mockVcState({
      savepoints: [{ sha: 'abc', message: 'snap', svnUrl: null, svnRevision: 3, projectChecksum: null, compileStatus: null, fSignature: null }],
    })
    const { host } = await render(<VersionControlPanel workbenchId="wb-1" worktreeId="wt-1" />)

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
    const { host } = await render(<VersionControlPanel workbenchId="wb-1" worktreeId="wt-1" />)

    expect(host.querySelector('[data-testid="vc-snapshot-drift"]')?.textContent).toContain('3 commits since')
  })

  it('switches to the history timeline through the tab bar', async () => {
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
    const { host } = await render(<VersionControlPanel workbenchId="wb-1" worktreeId="wt-1" />)

    await click(host.querySelector('[data-testid="vc-tab-history"]')!)

    expect(host.querySelector('[data-testid="vc-tab-history"]')?.getAttribute('aria-pressed')).toBe('true')
    expect(host.querySelector('[data-testid="commit-abcdef1"]')).toBeTruthy()
    expect(host.querySelector('[data-testid="savepoint-r4"]')).toBeTruthy()

    await click(host.querySelector('[data-testid="commit-abcdef1"]')!)
    expect(host.querySelector('[data-testid="timeline-detail"]')?.textContent).not.toContain('PLC_1:AA BB')
  })

  it('executes the TIA comparison directly from the header action', async () => {
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
    const { host } = await render(<VersionControlPanel workbenchId="wb-1" worktreeId="wt-1" />)

    await click(host.querySelector('[data-testid="vc-compare-open"]')!)

    expect(compare).toHaveBeenCalledTimes(1)
    // A clean comparison now shows the clean-state block instead of a blank area.
    expect(host.querySelector('[data-testid="vc-clean-state"]')?.textContent).toContain('TIA matches master')
    expect(host.querySelector('[data-testid="vc-changes-empty"]')).toBeNull()
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
    const { host } = await render(<VersionControlPanel workbenchId="wb-1" worktreeId="wt-1" />)

    await click(host.querySelector('[data-testid="vc-compare-open"]')!)

    expect(host.querySelector('[data-testid="vc-changes-empty"]')).toBeNull()
    expect(host.textContent).toContain('PLC_1 · Main')
    expect(host.textContent).not.toContain('TIA differs from master')
  })

  it('does not re-run the TIA comparison when switching between pages', async () => {
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
    const { host } = await render(<VersionControlPanel workbenchId="wb-1" worktreeId="wt-1" />)

    await click(host.querySelector('[data-testid="vc-compare-open"]')!)
    expect(compare).toHaveBeenCalledTimes(1)

    // Switching to history and back keeps the result instead of comparing again.
    await click(host.querySelector('[data-testid="vc-tab-history"]')!)
    await click(host.querySelector('[data-testid="vc-tab-changes"]')!)

    expect(compare).toHaveBeenCalledTimes(1)
    expect(host.querySelector('[data-testid="vc-clean-state"]')?.textContent).toContain('TIA matches master')
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
    const { host } = await render(<VersionControlPanel workbenchId="wb-1" worktreeId="wt-1" />)

    await click(host.querySelector('[data-testid="vc-tab-history"]')!)

    expect(host.querySelector('[data-testid="vc-untrackable-marker"]')?.textContent).toContain('untrackable')
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
    const { host } = await render(<VersionControlPanel workbenchId="wb-1" worktreeId="wt-1" />)

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
    const { host } = await render(<VersionControlPanel workbenchId="wb-1" worktreeId="wt-1" />)

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
    const { host } = await render(<VersionControlPanel workbenchId="wb-1" worktreeId="wt-1" />)

    expect(host.querySelector('[data-testid="vc-untrackable-savepoint-warning"]')).toBeTruthy()
  })
})
