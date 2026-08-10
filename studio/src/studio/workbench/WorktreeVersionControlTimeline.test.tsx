// @vitest-environment happy-dom
import React, { act } from 'react'
import { createRoot } from 'react-dom/client'
import { afterEach, describe, expect, it, vi } from 'vitest'
import * as api from '@/api/client'
import WorktreeVersionControlTimeline from './WorktreeVersionControlTimeline'

globalThis.IS_REACT_ACT_ENVIRONMENT = true

const gitCommit = (index: number, overrides: Partial<api.VersionControlTimelineGitCommit> = {}): api.VersionControlTimelineGitCommit => ({
  sha: `commit-${index}`,
  author: 'Ansel',
  message: `Commit ${index}`,
  timestamp: `2026-08-10T${String(8 + index).padStart(2, '0')}:00:00Z`,
  files: ['devices/PLC_1/source/Blocks/Main.xml'],
  tiaChecksum: null,
  svnRevision: null,
  ...overrides,
})

const firstPage: api.VersionControlTimelineResult = {
  gitCommits: Array.from({ length: 10 }, (_, index) => gitCommit(index)),
  svnRevisions: [{
    revision: 184,
    author: 'Ansel',
    message: 'Save TIA state',
    timestamp: '2026-08-10T08:00:00Z',
    tiaChecksum: 'PLC_1:checksum-1',
    gitCommitSha: 'commit-0',
  }],
  hasMore: true,
}

const secondPage: api.VersionControlTimelineResult = {
  gitCommits: [gitCommit(10, { sha: 'abcdef1234567890', message: 'Validate Main block', tiaChecksum: 'PLC_1:checksum-1' })],
  svnRevisions: [],
  hasMore: false,
}

const render = async () => {
  const host = document.createElement('div')
  document.body.appendChild(host)
  const root = createRoot(host)
  await act(async () => root.render(<WorktreeVersionControlTimeline workbenchId="wb-1" worktreeId="wt-1" />))
  await act(async () => {})
  return { host, root }
}

afterEach(() => {
  vi.restoreAllMocks()
  document.body.innerHTML = ''
})

describe('WorktreeVersionControlTimeline', () => {
  it('loads ten commits initially and shows linked Git/SVN labels', async () => {
    const load = vi.spyOn(api, 'getWorktreeVersionControlTimeline').mockResolvedValue(firstPage)
    const { host } = await render()

    expect(load).toHaveBeenCalledWith('wb-1', 'wt-1', 0, 10)
    expect(host.textContent).toContain('commit-0')
    expect(host.textContent).toContain('r184')
    expect(host.textContent).toContain('TIA PLC_1:checks…')
    expect(host.querySelector('[data-timeline-link="commit-0-r184"]')).not.toBeNull()
  })

  it('loads the next page without duplicating prior commits', async () => {
    const load = vi.spyOn(api, 'getWorktreeVersionControlTimeline')
      .mockResolvedValueOnce(firstPage)
      .mockResolvedValueOnce(secondPage)
    const { host } = await render()

    await act(async () => host.querySelector<HTMLButtonElement>('[data-testid="timeline-load-more"]')?.click())

    expect(load).toHaveBeenLastCalledWith('wb-1', 'wt-1', 10, 10)
    expect(host.querySelectorAll('[data-timeline-git]').length).toBe(11)
    expect(host.textContent).toContain('abcdef1')
    expect(host.querySelector('[data-testid="timeline-load-more"]')).toBeNull()
  })

  it('shows event details when a Git shape receives focus', async () => {
    vi.spyOn(api, 'getWorktreeVersionControlTimeline').mockResolvedValue({
      ...firstPage,
      gitCommits: [gitCommit(0, {
        sha: 'abcdef1234567890',
        message: 'Validate Main block',
        tiaChecksum: 'PLC_1:checksum-1',
      })],
      svnRevisions: [],
      hasMore: false,
    })
    const { host } = await render()

    await act(async () => host.querySelector<HTMLButtonElement>('[data-timeline-git="abcdef1234567890"]')?.focus())

    expect(host.textContent).toContain('Validate Main block')
    expect(host.textContent).toContain('Ansel')
    expect(host.textContent).toContain('Time:')
    expect(host.textContent).toContain('abcdef1234567890')
    expect(host.textContent).toContain('PLC_1:checksum-1')
  })
})
