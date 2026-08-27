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
  untrackableChange: null,
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
  it('shows a seven-character Git hash and device-free checksum in the metadata lane', async () => {
    const fullSha = 'abcdef1234567890abcdef1234567890abcdef12'
    const fullChecksum = 'PLC_1:0123456789abcdef0123456789abcdef'
    vi.spyOn(api, 'getWorktreeVersionControlTimeline').mockResolvedValue({
      ...firstPage,
      gitCommits: [gitCommit(0, {
        sha: fullSha,
        tiaChecksum: fullChecksum,
        timestamp: '2026-08-07T10:44:00',
      })],
      svnRevisions: [{
        ...firstPage.svnRevisions[0],
        gitCommitSha: fullSha,
        tiaChecksum: fullChecksum,
      }],
      hasMore: false,
    })
    const { host } = await render()

    expect(host.textContent).toContain('Git hash')
    expect(host.textContent).toContain('TIA checksum')
    expect(host.textContent).toContain('abcdef1')
    expect(host.textContent).toContain('0123456789abcdef0123456789abcdef')
    expect(host.textContent).not.toContain(fullSha)
    expect(host.textContent).not.toContain(fullChecksum)
    expect(host.querySelector('[data-timeline-git-hash]')?.textContent).toBe('abcdef1')
    expect(host.querySelector('[data-timeline-tia-checksum]')?.textContent).toBe('0123456789abcdef0123456789abcdef')
    expect(host.querySelector('[data-timeline-timestamp]')?.textContent).toBe('2026/8/7 10:44')
  })

  it('loads ten commits initially and shows linked Git/SVN labels', async () => {
    const load = vi.spyOn(api, 'getWorktreeVersionControlTimeline').mockResolvedValue(firstPage)
    const { host } = await render()

    expect(load).toHaveBeenCalledWith('wb-1', 'wt-1', 0, 10)
    expect(host.textContent).toContain('commit-')
    expect(host.textContent).toContain('r184')
    expect(host.textContent).toContain('Timestamp')
    expect(host.textContent).toContain('checksum-1')
    expect(host.textContent).not.toContain('PLC_1:checksum-1')
    expect(host.querySelectorAll('[data-timeline-column]').length).toBe(10)
    expect(host.querySelectorAll('[data-timeline-timestamp]').length).toBe(10)
    expect(host.querySelector('[data-testid="timeline-labels"]')?.className).toContain('shrink-0')
    expect(host.querySelector('[data-testid="timeline-scroll"]')?.className).toContain('overflow-x-auto')
    expect(host.querySelector('[data-timeline-column]')?.className).toContain('min-w-[176px]')
    expect(host.querySelector('[data-timeline-link="commit-0-r184"]')).toBeNull()
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

  it('marks columns whose commit records an untrackable change', async () => {
    vi.spyOn(api, 'getWorktreeVersionControlTimeline').mockResolvedValue({
      ...firstPage,
      gitCommits: [
        gitCommit(0, { sha: 'untrackable-1', untrackableChange: true }),
        gitCommit(1, { sha: 'ordinary-1' }),
      ],
      svnRevisions: [],
      hasMore: false,
    })
    const { host } = await render()

    const markers = host.querySelectorAll('[data-testid="vc-untrackable-marker"]')
    expect(markers.length).toBe(1)
    expect(markers[0].getAttribute('title')).toBe('Untrackable change — no git-file diff')
    expect(markers[0].closest('[data-timeline-column]')?.querySelector('[data-timeline-git-hash]')?.textContent).toBe('untrack')
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

  it('positions event details beside the pointer when a Git shape is hovered', async () => {
    vi.spyOn(api, 'getWorktreeVersionControlTimeline').mockResolvedValue({
      ...firstPage,
      gitCommits: [gitCommit(0, { sha: 'abcdef1234567890' })],
      svnRevisions: [],
      hasMore: false,
    })
    const { host } = await render()
    const shape = host.querySelector<HTMLButtonElement>('[data-timeline-git="abcdef1234567890"]')!

    await act(async () => shape.dispatchEvent(new MouseEvent('mouseover', { bubbles: true, clientX: 120, clientY: 240 })))

    const details = host.querySelector<HTMLElement>('[data-testid="timeline-event-details"]')
    expect(details).not.toBeNull()
    expect(details?.className).toContain('fixed')
    expect(details?.style.left).toBe('136px')
    expect(details?.style.top).toBe('256px')
  })

  it('shortens the linked Git hash in SVN hover details', async () => {
    const fullSha = 'abcdef1234567890abcdef1234567890abcdef12'
    vi.spyOn(api, 'getWorktreeVersionControlTimeline').mockResolvedValue({
      ...firstPage,
      gitCommits: [gitCommit(0, { sha: fullSha })],
      svnRevisions: [{ ...firstPage.svnRevisions[0], gitCommitSha: fullSha }],
      hasMore: false,
    })
    const { host } = await render()

    await act(async () => host.querySelector<HTMLButtonElement>('[data-timeline-svn="184"]')?.focus())

    const details = host.querySelector<HTMLElement>('[data-testid="timeline-event-details"]')
    const gitCommitDetail = [...(details?.querySelectorAll('span') ?? [])]
      .find(span => span.textContent?.startsWith('Git commit:'))
    expect(gitCommitDetail?.textContent).toBe('Git commit: abcdef1')
    expect(gitCommitDetail?.getAttribute('title')).toBe(fullSha)
  })
})
