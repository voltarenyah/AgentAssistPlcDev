// @vitest-environment happy-dom
import React, { act } from 'react'
import { createRoot } from 'react-dom/client'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import * as api from '@/api/client'
import WorktreeLandingPage from './WorktreeLandingPage'

globalThis.IS_REACT_ACT_ENVIRONMENT = true

const detail: api.WorktreeDetail = {
  worktreeId: 'wt1',
  workbenchId: 'wb1',
  name: 'feature-a',
  branch: 'feature/a',
  createdAt: '2026-07-15T00:00:00Z',
  baseCommit: 'abc123def',
  engineeringProjectId: null,
  sourceProjectPath: 'D:/proj.ap17',
  deviceIds: ['dev1', 'dev2'],
  lastReconciliationCommit: null,
  purpose: 'Rework motor control',
  owner: 'Bo',
  status: 'ongoing',
  finishedUtc: null,
}

const task = (overrides: Partial<api.WorktreeTask>): api.WorktreeTask => ({
  taskId: 'task-1',
  title: 'Task',
  details: null,
  status: 'todo',
  elementRefs: [],
  createdUtc: '2026-08-01T00:00:00Z',
  doneUtc: null,
  ...overrides,
})

const taskList: api.WorktreeTaskList = {
  version: 1,
  tasks: [
    task({ taskId: 't1', title: 'Open task', status: 'todo' }),
    task({ taskId: 't2', title: 'Running task', status: 'inProgress' }),
    task({ taskId: 't3', title: 'Done task', status: 'done' }),
  ],
}

const snapshot = (deviceId: string, plcName: string, modified: string[]): api.DeviceSnapshot => ({
  workbenchId: 'wb1',
  worktreeId: 'wt1',
  deviceId,
  plcName,
  engineeringIdentity: plcName,
  exportedSourceRoot: 'C:/wb/exported',
  modifiedSourceRoot: 'C:/wb/modified',
  knowledgeDbPath: 'C:/wb/plc-knowledge.db',
  sourceProjectPath: 'D:/proj.ap17',
  device: null,
  knowledge: { state: 'missing', updatedAt: null },
  blocks: modified.map((name, index) => ({
    id: `b${index}`,
    name,
    number: index + 1,
    blockType: 'FB',
    programmingLanguage: 'SCL',
    groupPath: null,
    relativePath: `Blocks/${name}.xml`,
    modified: true,
  })),
  overlayCount: modified.length,
  diagnostics: [],
})

vi.mock('@/api/client', async importOriginal => {
  const actual = await importOriginal<typeof import('@/api/client')>()
  return {
    ...actual,
    getWorktreeDetail: vi.fn(async () => detail),
    updateWorktree: vi.fn(async (_wb: string, _wt: string, patch: { status?: api.WorktreeStatus }) => ({
      ...detail,
      status: patch.status ?? detail.status,
      finishedUtc: patch.status === 'finished' ? '2026-08-03T00:00:00Z' : detail.finishedUtc,
    })),
    listWorktreeTasks: vi.fn(async () => taskList),
    getWorktreeVersionControlTimeline: vi.fn(async () => ({
      gitCommits: [],
      svnRevisions: [],
      hasMore: false,
    } as api.VersionControlTimelineResult)),
    listDevices: vi.fn(async () => [
      { deviceId: 'dev1', plcName: 'PLC_One' },
      { deviceId: 'dev2', plcName: 'PLC_Two' },
    ] as api.DeviceSummary[]),
    getDeviceInfo: vi.fn(async (_wb: string, _wt: string, deviceId: string) =>
      deviceId === 'dev1'
        ? snapshot('dev1', 'PLC_One', ['FB_Motor_Control', 'FB_Alarm'])
        : snapshot('dev2', 'PLC_Two', [])),
  }
})

const renderPage = async (overrides: Partial<React.ComponentProps<typeof WorktreeLandingPage>> = {}) => {
  const onTabChange = vi.fn()
  const onSelectDevice = vi.fn()
  const host = document.createElement('div')
  document.body.appendChild(host)
  const root = createRoot(host)
  await act(async () => root.render(
    <WorktreeLandingPage
      workbenchId="wb1"
      worktreeId="wt1"
      tab="overview"
      onTabChange={onTabChange}
      onSelectDevice={onSelectDevice}
      {...overrides}
    />,
  ))
  // Flush detail + tasks + device snapshot promise chains.
  await act(async () => {})
  await act(async () => {})
  await act(async () => {})
  return { host, root, onTabChange, onSelectDevice }
}

afterEach(() => {
  document.body.innerHTML = ''
})

beforeEach(() => {
  vi.clearAllMocks()
})

describe('WorktreeLandingPage', () => {
  it('renders the header card and metadata fields', async () => {
    const { host, root } = await renderPage()

    expect(host.textContent).toContain('feature-a')
    expect(host.textContent).toContain('feature/a')
    expect(host.textContent).toContain('abc123def')
    expect(host.textContent).toContain('D:/proj.ap17')
    expect((host.querySelector('textarea[aria-label="Worktree purpose"]') as HTMLTextAreaElement).value)
      .toBe('Rework motor control')
    expect((host.querySelector('input[aria-label="Worktree owner"]') as HTMLInputElement).value).toBe('Bo')

    await act(async () => root.unmount())
  })

  it('renders version-control history between metadata and tasks on the overview', async () => {
    const { host, root } = await renderPage()

    expect(host.textContent).toContain('Worktree version control')
    const sections = [...host.querySelectorAll('section')]
    const contextIndex = sections.findIndex(section => section.getAttribute('data-testid') === 'worktree-context')
    const metadataIndex = sections.findIndex(section => section.textContent?.includes('Worktree metadata'))
    const timelineIndex = sections.findIndex(section => section.getAttribute('aria-label') === 'Worktree version control')
    const tasksIndex = sections.findIndex(section => section.textContent?.includes('Open task list'))
    expect(contextIndex).toBe(metadataIndex)
    expect(sections[contextIndex]?.querySelector('[aria-label="Worktree purpose"]')).not.toBeNull()
    expect(sections[contextIndex]?.querySelector('[aria-label="Worktree owner"]')).not.toBeNull()
    expect(metadataIndex).toBeLessThan(timelineIndex)
    expect(timelineIndex).toBeLessThan(tasksIndex)

    await act(async () => root.unmount())
  })

  it('shows task counts in the summary strip and jumps to the tasks tab', async () => {
    const { host, root, onTabChange } = await renderPage()

    const strip = [...host.querySelectorAll('button')].find(button => button.textContent?.replace(/\d/g, '').trim() === 'In Progress')!
    expect(strip.textContent).toContain('1')
    await act(async () => strip.dispatchEvent(new MouseEvent('click', { bubbles: true })))
    expect(onTabChange).toHaveBeenCalledWith('tasks')

    await act(async () => root.unmount())
  })

  it('renders the grouped task panel on the tasks tab', async () => {
    const { host, root } = await renderPage({ tab: 'tasks' })

    expect(host.textContent).toContain('Open task')
    expect(host.textContent).toContain('Running task')
    expect(host.textContent).toContain('Done task')

    await act(async () => root.unmount())
  })

  it('lists devices with overlay-modified blocks and selects them on click', async () => {
    const { host, root, onSelectDevice } = await renderPage()

    expect(host.textContent).toContain('PLC_One — 2 modified blocks')
    expect(host.textContent).toContain('FB_Motor_Control')
    expect(host.textContent).toContain('FB_Alarm')
    expect(host.textContent).not.toContain('PLC_Two —')

    const row = [...host.querySelectorAll('button')].find(button => button.textContent?.includes('PLC_One —'))!
    await act(async () => row.dispatchEvent(new MouseEvent('click', { bubbles: true })))
    expect(onSelectDevice).toHaveBeenCalledWith('dev1')

    await act(async () => root.unmount())
  })

  it('changes the worktree status through the badge', async () => {
    const { host, root } = await renderPage()
    const trigger = host.querySelector('button[aria-label="Change worktree status"]') as HTMLButtonElement

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

    expect(vi.mocked(api.updateWorktree)).toHaveBeenCalledWith('wb1', 'wt1', { status: 'finished' })
    expect(host.textContent).toContain('Finished')

    await act(async () => root.unmount())
  })

  it('shows an error state when the worktree cannot be loaded', async () => {
    vi.mocked(api.getWorktreeDetail).mockRejectedValueOnce(new Error('gone'))
    const { host, root } = await renderPage()

    expect(host.textContent).toContain('Worktree unavailable')
    expect(host.textContent).toContain('gone')

    await act(async () => root.unmount())
  })
})
