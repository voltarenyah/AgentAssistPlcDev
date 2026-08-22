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

  it('switches to the history timeline through the tab bar', async () => {
    mockVcState({
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
    // The result renders inline on the changes page — no overlay, no navigation.
    expect(host.querySelector('[data-testid="vc-compare-result"]')).toBeTruthy()
    expect(host.textContent).toContain('TIA matches master')
    expect(host.querySelector('[data-testid="vc-changes-empty"]')).toBeTruthy()
  })
})
