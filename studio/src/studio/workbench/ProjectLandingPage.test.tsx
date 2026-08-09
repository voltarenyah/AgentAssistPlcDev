// @vitest-environment happy-dom
import React, { act } from 'react'
import { createRoot } from 'react-dom/client'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import * as api from '@/api/client'
import ProjectLandingPage from './ProjectLandingPage'

globalThis.IS_REACT_ACT_ENVIRONMENT = true

const worktree = (overrides: Partial<api.WorktreeOverview>): api.WorktreeOverview => ({
  worktreeId: 'wt-1',
  name: 'master',
  branch: 'master',
  relativePath: 'worktrees/master',
  createdAt: '2026-07-01T00:00:00Z',
  purpose: null,
  owner: null,
  status: 'ongoing',
  finishedUtc: null,
  openTasks: 0,
  totalTasks: 0,
  ...overrides,
})

const overview: api.WorkbenchOverview = {
  workbenchId: 'wb1',
  name: 'DemoWB',
  createdAt: '2026-07-30T00:00:00Z',
  rootPath: 'C:/wb',
  repositoryPath: 'C:/wb/repo',
  engineeringProjectId: null,
  sourceProjectPath: 'D:/proj.ap17',
  purpose: 'Line upgrade',
  owner: 'Ansel',
  worktrees: [
    worktree({
      worktreeId: 'wt-old',
      name: 'old-fix',
      branch: 'fix/old',
      status: 'finished',
      finishedUtc: '2026-07-10T00:00:00Z',
    }),
    worktree({
      worktreeId: 'wt-live',
      name: 'feature-a',
      branch: 'feature/a',
      purpose: 'Rework motor control',
      owner: 'Bo',
      openTasks: 2,
      totalTasks: 5,
    }),
    worktree({
      worktreeId: 'wt-new',
      name: 'new-fix',
      branch: 'fix/new',
      status: 'finished',
      finishedUtc: '2026-08-01T00:00:00Z',
    }),
  ],
}

vi.mock('@/api/client', async importOriginal => {
  const actual = await importOriginal<typeof import('@/api/client')>()
  return {
    ...actual,
    getWorkbenchOverview: vi.fn(async () => overview),
    updateWorkbench: vi.fn(async () => overview),
    updateWorktree: vi.fn(async () => ({} as api.WorktreeDetail)),
  }
})

const render = async (element: React.ReactNode) => {
  const host = document.createElement('div')
  document.body.appendChild(host)
  const root = createRoot(host)
  await act(async () => root.render(element))
  await act(async () => {})
  return { host, root }
}

const setInputValue = (input: HTMLInputElement | HTMLTextAreaElement, value: string) => {
  const prototype = input instanceof HTMLTextAreaElement
    ? window.HTMLTextAreaElement.prototype
    : window.HTMLInputElement.prototype
  const setter = Object.getOwnPropertyDescriptor(prototype, 'value')!.set!
  setter.call(input, value)
  input.dispatchEvent(new Event('input', { bubbles: true }))
}

afterEach(() => {
  document.body.innerHTML = ''
})

beforeEach(() => {
  vi.clearAllMocks()
})

describe('ProjectLandingPage', () => {
  it('renders the project header with metadata, purpose and owner', async () => {
    const { host, root } = await render(<ProjectLandingPage workbenchId="wb1" onSelectWorktree={() => {}} />)

    expect(host.textContent).toContain('DemoWB')
    expect(host.textContent).toContain('C:/wb')
    expect(host.textContent).toContain('D:/proj.ap17')
    expect((host.querySelector('input[aria-label="Project purpose"]') as HTMLInputElement).value).toBe('Line upgrade')
    expect((host.querySelector('input[aria-label="Project owner"]') as HTMLInputElement).value).toBe('Ansel')

    await act(async () => root.unmount())
  })

  it('opens the workbench assistant from the project landing page', async () => {
    const onOpenAssistant = vi.fn()
    const { host, root } = await render(
      <ProjectLandingPage workbenchId="wb1" onSelectWorktree={() => {}} onOpenAssistant={onOpenAssistant} />,
    )

    const button = host.querySelector<HTMLButtonElement>('button[aria-label="Open Workbench Assistant"]')
    expect(button).not.toBeNull()
    await act(async () => button!.click())

    expect(onOpenAssistant).toHaveBeenCalledTimes(1)

    await act(async () => root.unmount())
  })

  it('orders ongoing worktrees first, then finished by finishedUtc descending', async () => {
    const { host, root } = await render(<ProjectLandingPage workbenchId="wb1" onSelectWorktree={() => {}} />)

    const rows = [...host.querySelectorAll('tbody tr')].map(row => row.querySelector('td')!.textContent)
    expect(rows).toEqual(['feature-a', 'new-fix', 'old-fix'])
    expect(host.textContent).toContain('2 / 5')

    await act(async () => root.unmount())
  })

  it('selects a worktree when its row is clicked', async () => {
    const onSelectWorktree = vi.fn()
    const { host, root } = await render(<ProjectLandingPage workbenchId="wb1" onSelectWorktree={onSelectWorktree} />)

    const row = [...host.querySelectorAll('tbody tr')].find(element => element.textContent?.includes('feature-a'))!
    await act(async () => row.dispatchEvent(new MouseEvent('click', { bubbles: true })))

    expect(onSelectWorktree).toHaveBeenCalledWith('wt-live')

    await act(async () => root.unmount())
  })

  it('saves the purpose on blur through the workbench PATCH', async () => {
    const { host, root } = await render(<ProjectLandingPage workbenchId="wb1" onSelectWorktree={() => {}} />)
    const input = host.querySelector('input[aria-label="Project purpose"]') as HTMLInputElement

    await act(async () => setInputValue(input, 'Commissioning phase'))
    await act(async () => {
      input.dispatchEvent(new FocusEvent('focusout', { bubbles: true }))
    })
    await act(async () => {})

    expect(vi.mocked(api.updateWorkbench)).toHaveBeenCalledWith('wb1', { purpose: 'Commissioning phase' })

    await act(async () => root.unmount())
  })

  it('changes a worktree status from the table badge', async () => {
    const { host, root } = await render(<ProjectLandingPage workbenchId="wb1" onSelectWorktree={() => {}} />)

    const row = [...host.querySelectorAll('tbody tr')].find(element => element.textContent?.includes('feature-a'))!
    const trigger = row.querySelector('button[aria-label="Change worktree status"]') as HTMLButtonElement
    await act(async () => {
      trigger.dispatchEvent(new PointerEvent('pointerdown', { bubbles: true, button: 0, ctrlKey: false }))
      trigger.dispatchEvent(new MouseEvent('click', { bubbles: true }))
    })
    const item = Array.from(document.body.querySelectorAll<HTMLElement>('[role="menuitem"]'))
      .find(element => element.textContent?.trim() === 'Finished')!
    await act(async () => {
      item.dispatchEvent(new MouseEvent('click', { bubbles: true }))
    })
    await act(async () => {})

    expect(vi.mocked(api.updateWorktree)).toHaveBeenCalledWith('wb1', 'wt-live', { status: 'finished' })

    await act(async () => root.unmount())
  })

  it('shows an empty state when the project has no worktrees', async () => {
    vi.mocked(api.getWorkbenchOverview).mockResolvedValueOnce({ ...overview, worktrees: [] })
    const { host, root } = await render(<ProjectLandingPage workbenchId="wb1" onSelectWorktree={() => {}} />)

    expect(host.textContent).toContain('No worktrees yet')

    await act(async () => root.unmount())
  })

  it('shows an error state when the overview cannot be loaded', async () => {
    vi.mocked(api.getWorkbenchOverview).mockRejectedValueOnce(new Error('boom'))
    const { host, root } = await render(<ProjectLandingPage workbenchId="wb1" onSelectWorktree={() => {}} />)

    expect(host.textContent).toContain('Project overview unavailable')
    expect(host.textContent).toContain('boom')

    await act(async () => root.unmount())
  })
})
