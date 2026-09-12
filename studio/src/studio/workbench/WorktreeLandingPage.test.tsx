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
const projectGraphTask: api.EngineeringTask = {
  taskId: 'project-1', workbenchId: 'wb1', scope: 'project', worktreeId: null,
  title: 'Project improvement', type: 'improvement', status: 'todo', priority: 1,
  intent: 'Improve', expectedResult: 'Better', description: 'Project details',
  createdUtc: '2026-08-01T00:00:00Z', updatedUtc: '2026-08-01T00:00:00Z',
}
let activeGraphTask: api.EngineeringTask | null = null

const tagNodes: api.TagNode[] = [
  { tagId: 'tag-machine', parentTagId: null, name: 'Machine', normalizedName: 'machine' },
  { tagId: 'tag-press', parentTagId: 'tag-machine', name: 'Press', normalizedName: 'press' },
  { tagId: 'tag-state', parentTagId: null, name: 'State', normalizedName: 'state' },
]

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
    getTagTaxonomy: vi.fn(async () => ({ nodes: tagNodes })),
    createTagPath: vi.fn(async (path: string) => ({
      tagId: 'tag-created',
      parentTagId: 'tag-machine',
      name: path.split('/').at(-1)!,
      normalizedName: path.split('/').at(-1)!.toLowerCase(),
    })),
    getWorktreeTags: vi.fn(async () => ({ direct: ['tag-press'], inherited: ['tag-state'], effective: ['tag-press', 'tag-state'] })),
    assignWorktreeTag: vi.fn(async () => undefined),
    unassignWorktreeTag: vi.fn(async () => undefined),
    unassignWorkbenchTag: vi.fn(async () => undefined),
    updateWorktree: vi.fn(async (_wb: string, _wt: string, patch: { status?: api.WorktreeStatus }) => ({
      ...detail,
      status: patch.status ?? detail.status,
      finishedUtc: patch.status === 'finished' ? '2026-08-03T00:00:00Z' : detail.finishedUtc,
    })),
    listWorktreeTasks: vi.fn(async () => taskList),
    listProjectTasks: vi.fn(async () => [projectGraphTask]),
    listGraphWorktreeTasks: vi.fn(async () => [...taskList.tasks.map(item => ({
      ...item, workbenchId: 'wb1', scope: 'worktree' as const, worktreeId: 'wt1', type: 'feature' as const,
      priority: 0, intent: 'intent', expectedResult: 'result', updatedUtc: item.createdUtc,
    })), {
      ...taskList.tasks[0], taskId: 'other-worktree', title: 'Other worktree task', workbenchId: 'wb1',
      scope: 'worktree' as const, worktreeId: 'wt2', type: 'issue' as const,
      priority: 0, intent: 'intent', expectedResult: 'result', updatedUtc: taskList.tasks[0].createdUtc,
    }]),
    getActiveWorktreeTask: vi.fn(async () => ({ activeTask: activeGraphTask })),
    setActiveWorktreeTask: vi.fn(async (_wb: string, _wt: string, taskId: string | null) => ({
      activeTask: taskId === projectGraphTask.taskId ? projectGraphTask : taskId ? {
        ...projectGraphTask, taskId, title: 'Running task', scope: 'worktree' as const, worktreeId: 'wt1', type: 'feature' as const,
      } : null,
    })),
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
  activeGraphTask = null
})

describe('WorktreeLandingPage', () => {
  it('renders direct tags as removable and inherited tags as labelled non-removable chips', async () => {
    const { host, root } = await renderPage()

    expect(host.querySelector('[data-tag-id="tag-press"]')?.textContent).toContain('Machine/Press')
    expect(host.querySelector('button[aria-label="Remove tag Machine/Press"]')).not.toBeNull()
    expect(host.querySelector('[data-tag-id="tag-state"]')?.textContent).toContain('Inherited from Project')
    expect(host.querySelectorAll('[data-tag-id="tag-state"]')).toHaveLength(1)
    expect(host.textContent?.match(/Inherited from Project/g)).toHaveLength(1)
    expect(host.querySelector('[data-tag-id="tag-state"] button')).toBeNull()
    expect(host.querySelector('button[aria-label="Add tag"]')).not.toBeNull()

    await act(async () => (host.querySelector('button[aria-label="Add tag"]') as HTMLButtonElement).click())
    expect(document.body.querySelector('[role="treeitem"][aria-selected="true"] button[aria-label="Select Machine/Press"]')).not.toBeNull()

    await act(async () => root.unmount())
  })

  it('assigns and removes only direct Worktree tags while preserving the page', async () => {
    vi.mocked(api.getWorktreeTags)
      .mockResolvedValueOnce({ direct: ['tag-press'], inherited: ['tag-state'], effective: ['tag-press', 'tag-state'] })
      .mockResolvedValueOnce({ direct: ['tag-press', 'tag-machine'], inherited: ['tag-state'], effective: ['tag-press', 'tag-machine', 'tag-state'] })
      .mockResolvedValueOnce({ direct: [], inherited: ['tag-state'], effective: ['tag-state'] })
    const { host, root } = await renderPage()

    const add = host.querySelector('button[aria-label="Add tag"]') as HTMLButtonElement
    await act(async () => add.click())
    const machineOption = document.body.querySelector('button[aria-label="Select Machine"]') as HTMLElement
    await act(async () => machineOption?.click())
    await act(async () => {})

    expect(api.assignWorktreeTag).toHaveBeenCalledWith('wb1', 'wt1', 'tag-machine')
    expect(api.unassignWorkbenchTag).not.toHaveBeenCalled()
    expect(host.querySelector('input[aria-label="Worktree owner"]')).toHaveProperty('value', 'Bo')
    expect(host.querySelector('[data-tag-id="tag-machine"]')).not.toBeNull()

    const remove = host.querySelector('button[aria-label="Remove tag Machine/Press"]') as HTMLButtonElement
    await act(async () => remove.click())
    await act(async () => {})
    expect(api.unassignWorktreeTag).toHaveBeenCalledWith('wb1', 'wt1', 'tag-press')
    expect(api.unassignWorkbenchTag).not.toHaveBeenCalled()
    expect(host.querySelector('[data-tag-id="tag-state"] button')).toBeNull()

    await act(async () => root.unmount())
  })

  it('creates an absent hierarchical path and assigns the returned tag', async () => {
    const { host, root } = await renderPage()
    const add = host.querySelector('button[aria-label="Add tag"]') as HTMLButtonElement
    await act(async () => add.click())
    const input = document.body.querySelector('input[aria-label="Search tags"]') as HTMLInputElement
    const setter = Object.getOwnPropertyDescriptor(window.HTMLInputElement.prototype, 'value')!.set!
    setter.call(input, 'Machine/New')
    await act(async () => input.dispatchEvent(new Event('input', { bubbles: true })))

    const create = document.body.querySelector('[role="option"][aria-label="Create tag Machine/New"]') as HTMLElement
    expect(create).not.toBeNull()
    await act(async () => create.click())
    await act(async () => {})

    expect(api.createTagPath).toHaveBeenCalledWith('Machine/New')
    expect(api.assignWorktreeTag).toHaveBeenCalledWith('wb1', 'wt1', 'tag-created')
    expect(host.textContent).toContain('feature-a')

    await act(async () => root.unmount())
  })

  it('shows tag loading and recovers from a tag API error without hiding worktree details', async () => {
    let resolveTags!: (value: api.EntityTags) => void
    vi.mocked(api.getWorktreeTags).mockReturnValueOnce(new Promise(resolve => { resolveTags = resolve }))
    const { host, root } = await renderPage()
    expect(host.querySelector('button[aria-label="Add tag (loading)"]') ?? host.querySelector('button[aria-label="Add tag"]')).not.toBeNull()
    resolveTags({ direct: ['tag-press'], inherited: ['tag-state'], effective: ['tag-press', 'tag-state'] })
    await act(async () => {})
    expect(host.textContent).toContain('Machine/Press')
    expect(host.textContent).toContain('feature-a')

    await act(async () => root.unmount())
  })

  it('shows a tag API error while preserving loaded worktree details', async () => {
    vi.mocked(api.getTagTaxonomy).mockRejectedValueOnce(new Error('tag service down'))
    const { host, root } = await renderPage()

    expect(host.textContent).toContain('feature-a')
    expect(host.querySelector('[role="alert"]')?.textContent).toContain('tag service down')

    await act(async () => root.unmount())
  })

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

  it('keeps version-control history in the right dock instead of duplicating it on the overview', async () => {
    const { host, root } = await renderPage()

    expect(host.textContent).not.toContain('Worktree version control')
    const sections = [...host.querySelectorAll('section')]
    const contextIndex = sections.findIndex(section => section.getAttribute('data-testid') === 'worktree-context')
    const metadataIndex = sections.findIndex(section => section.textContent?.includes('Worktree metadata'))
    const timelineIndex = sections.findIndex(section => section.getAttribute('aria-label') === 'Worktree version control')
    const tasksIndex = sections.findIndex(section => section.textContent?.includes('Open task list'))
    expect(contextIndex).toBe(metadataIndex)
    expect(sections[contextIndex]?.querySelector('[aria-label="Worktree purpose"]')).not.toBeNull()
    expect(sections[contextIndex]?.querySelector('[aria-label="Worktree owner"]')).not.toBeNull()
    expect(timelineIndex).toBe(-1)
    expect(metadataIndex).toBeLessThan(tasksIndex)

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

  it('wires graph scopes/types and active task keyboard selection and clear through the landing page', async () => {
    const { host, root } = await renderPage({ tab: 'tasks' })
    expect(host.textContent).toContain('Project improvement')
    expect(host.textContent).toContain('Project · Improvement')
    expect(host.textContent).toContain('Improvement')
    expect(host.textContent).toContain('Worktree scope')
    const selector = host.querySelector('select[aria-label="Active task"]') as HTMLSelectElement
    expect(Array.from(selector.options).some(option => option.value === 'other-worktree')).toBe(false)
    await act(async () => {
      selector.focus()
      selector.dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowDown', bubbles: true }))
      selector.value = 'project-1'
      selector.dispatchEvent(new Event('change', { bubbles: true }))
    })
    expect(vi.mocked(api.setActiveWorktreeTask)).toHaveBeenCalledWith('wb1', 'wt1', 'project-1')
    expect(host.textContent).toContain('Project improvement')
    await act(async () => {
      const clear = [...host.querySelectorAll('button')].find(button => button.textContent?.trim() === 'Clear') as HTMLButtonElement
      clear.focus()
      clear.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true }))
    })
    expect(vi.mocked(api.setActiveWorktreeTask)).toHaveBeenCalledWith('wb1', 'wt1', null)
    await act(async () => root.unmount())
  })

  it('keeps the active selection and shows recovery when the wired mutation fails', async () => {
    activeGraphTask = projectGraphTask
    vi.mocked(api.setActiveWorktreeTask).mockRejectedValueOnce(new Error('graph unavailable'))
    const { host, root } = await renderPage({ tab: 'tasks' })
    const selector = host.querySelector('select[aria-label="Active task"]') as HTMLSelectElement
    expect(selector.value).toBe('project-1')
    await act(async () => { selector.value = ''; selector.dispatchEvent(new Event('change', { bubbles: true })) })
    expect(selector.value).toBe('project-1')
    expect(host.querySelector('[role="alert"]')?.textContent).toContain('graph unavailable')
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
