// @vitest-environment happy-dom
import React, { act } from 'react'
import { createRoot } from 'react-dom/client'
import { afterEach, describe, expect, it, vi } from 'vitest'
import * as api from '@/api/client'
import AllProjectsLandingPage from './AllProjectsLandingPage'

const projects: api.WorkbenchLandingCard[] = [
  { workbenchId: 'wb-old', name: 'Old', createdAt: '2026-05-01T00:00:00Z', updatedAt: null, modifiedAt: '2026-02-01T00:00:00Z', purpose: null, owner: null, coverAssetId: 'old.png', effectiveTagIds: ['tag-line'], worktrees: [{ worktreeId: 'wt-old', name: 'master', branch: 'master', createdAt: null, updatedAt: null, completedTasks: 0, totalTasks: 0, dirtySourceFiles: 0, sessionCount: 0, availability: 'available' }] },
  { workbenchId: 'wb-new', name: 'New', createdAt: '2026-01-01T00:00:00Z', updatedAt: null, modifiedAt: '2026-04-01T00:00:00Z', purpose: null, owner: null, coverAssetId: null, effectiveTagIds: ['tag-machine'], worktrees: [{ worktreeId: 'wt-new', name: 'feature', branch: 'feature', createdAt: null, updatedAt: null, completedTasks: 1, totalTasks: 2, dirtySourceFiles: 2, sessionCount: 3, availability: 'available' }] },
]

const render = async (props: Partial<React.ComponentProps<typeof AllProjectsLandingPage>> = {}) => {
  const host = document.createElement('div'); document.body.appendChild(host); const root = createRoot(host)
  await act(async () => root.render(<AllProjectsLandingPage projects={projects} tagNodes={[{ tagId: 'tag-line', parentTagId: null, name: 'Line', normalizedName: 'line' }, { tagId: 'tag-machine', parentTagId: null, name: 'Machine', normalizedName: 'machine' }]} onSelectWorkbench={vi.fn()} onSelectWorktree={vi.fn()} {...props} />))
  return { host, root }
}

afterEach(() => { document.body.innerHTML = '' })

describe('AllProjectsLandingPage', () => {
  it('shows server tag identities, sorts by activity/creation, and labels zero-task rings', async () => {
    const { host } = await render()
    expect(host.textContent).toContain('Line')
    expect(host.textContent).toContain('Machine')
    expect(host.textContent).toContain('No tasks')
    expect(host.querySelector('[aria-label="No tasks"]')).not.toBeNull()
    const cards = [...host.querySelectorAll('article h2')].map(item => item.textContent)
    expect(cards).toEqual(['New', 'Old'])
    const sort = host.querySelector('#landing-sort') as HTMLSelectElement
    await act(async () => { sort.value = 'created'; sort.dispatchEvent(new Event('change', { bubbles: true })) })
    expect([...host.querySelectorAll('article h2')].map(item => item.textContent)).toEqual(['Old', 'New'])
  })

  it('routes Project and nested worktree actions separately and filters by server IDs', async () => {
    const selectWorkbench = vi.fn(); const selectWorktree = vi.fn()
    const { host } = await render({ onSelectWorkbench: selectWorkbench, onSelectWorktree: selectWorktree, matchingWorkbenchIds: ['wb-new'] })
    expect(host.textContent).toContain('New'); expect(host.textContent).not.toContain('Old')
    await act(async () => (host.querySelector('article button') as HTMLButtonElement).click())
    await act(async () => (host.querySelectorAll('article button')[2] as HTMLButtonElement).click())
    expect(selectWorkbench).toHaveBeenCalledWith('wb-new'); expect(selectWorktree).toHaveBeenCalledWith('wb-new', 'wt-new')
  })

  it('keeps the existing cover on a failed upload and announces the error', async () => {
    vi.spyOn(api, 'uploadWorkbenchCover').mockRejectedValueOnce(new Error('invalid image'))
    const { host } = await render()
    await act(async () => (host.querySelector('button[aria-label="Choose cover image for Old"]') as HTMLButtonElement).click())
    const input = host.querySelector('input[type=file]') as HTMLInputElement
    const file = new File(['x'], 'cover.png', { type: 'image/png' })
    await act(async () => { Object.defineProperty(input, 'files', { value: [file] }); input.dispatchEvent(new Event('change', { bubbles: true })) })
    await act(async () => { await Promise.resolve(); await Promise.resolve() })
    expect(host.querySelector('[role="alert"]')?.textContent).toContain('invalid image')
    expect(host.querySelector('img')?.getAttribute('alt')).toContain('Old cover')
  })
})
