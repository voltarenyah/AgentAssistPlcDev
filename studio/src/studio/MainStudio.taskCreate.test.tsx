// @vitest-environment happy-dom
import React, { act } from 'react'
import { createRoot } from 'react-dom/client'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import * as api from '@/api/client'
import MainStudio from './MainStudio'

globalThis.IS_REACT_ACT_ENVIRONMENT = true

const workbench: api.Workbench = {
  schemaVersion: '1.0',
  workbenchId: 'wb1',
  name: 'DemoWB',
  createdAt: '2026-07-30T00:00:00Z',
  rootPath: 'C:/wb',
  worktrees: [{ worktreeId: 'wt1', name: 'master', branch: 'master', relativePath: 'worktrees/master' }],
}

const worktreeDetail: api.WorktreeDetail = {
  worktreeId: 'wt1', workbenchId: 'wb1', name: 'master', branch: 'master',
  createdAt: '2026-08-01T00:00:00Z', baseCommit: null, engineeringProjectId: null,
  sourceProjectPath: null, deviceIds: ['dev1'], lastReconciliationCommit: null,
  purpose: null, owner: null, status: 'active', finishedUtc: null,
}

const snapshot: api.DeviceSnapshot = {
  workbenchId: 'wb1',
  worktreeId: 'wt1',
  deviceId: 'dev1',
  plcName: 'PLC_Demo',
  engineeringIdentity: 'PLC_Demo',
  sourceRoot: 'C:/wb/source',
  knowledgeDbPath: 'C:/wb/plc-knowledge.db',
  sourceProjectPath: 'D:/proj.ap17',
  device: null,
  knowledge: { state: 'missing', updatedAt: null },
  blocks: [],
  sourceObjectCount: 0,
  diagnostics: [],
}

const task: api.EngineeringTask = {
  taskId: 'task1',
  workbenchId: 'wb1',
  scope: 'worktree',
  worktreeId: 'wt1',
  title: 'Inspect startup sequence',
  type: 'feature',
  status: 'todo',
  priority: 0,
  intent: '',
  expectedResult: '',
  description: null,
  createdUtc: '2026-08-02T00:00:00.000Z',
  updatedUtc: '2026-08-02T00:00:00.000Z',
  deviceId: 'dev1',
}

vi.mock('@/api/client', async importOriginal => {
  const actual = await importOriginal<typeof import('@/api/client')>()
  return {
    ...actual,
    listWorkbenches: vi.fn(async () => [workbench]),
    selectWorkbench: vi.fn(async () => ({})),
    selectWorktree: vi.fn(async () => ({})),
    getWorktreeDetail: vi.fn(async () => worktreeDetail),
    getTagTaxonomy: vi.fn(async () => ({ nodes: [] })),
    getWorktreeTags: vi.fn(async () => ({ direct: [], inherited: [] })),
    listProjectTasks: vi.fn(async () => []),
    listGraphWorktreeTasks: vi.fn(async () => [task]),
    listDevices: vi.fn(async () => [{ deviceId: 'dev1', plcName: 'PLC_Demo' }]),
    getEngineeringTaskDetail: vi.fn(async () => ({ task, sessions: [], commits: [], sourceObjects: [], svnRevisions: [] })),
    getDeviceInfo: vi.fn(async () => snapshot),
    listDeviceSessions: vi.fn(async () => []),
    getKeyStatus: vi.fn(async () => ({ configured: true })),
    getDeepSeekBalance: vi.fn(async () => ({ isAvailable: true, balances: [], fetchedAt: '2026-08-02T00:00:00.000Z' })),
    getSessions: vi.fn(async () => []),
    createGraphWorktreeTask: vi.fn(async () => ({})),
  }
})

// happy-dom has no layout engine; swap in the lightweight FlexLayout stand-in.
vi.mock('flexlayout-react', async () => await import('@/test/flexLayoutMock'))

const mountedRoots: ReturnType<typeof createRoot>[] = []

const render = (element: React.ReactNode) => {
  const host = document.createElement('div')
  document.body.appendChild(host)
  const root = createRoot(host)
  mountedRoots.push(root)
  act(() => root.render(element))
  return { host, root }
}

const clickText = (host: HTMLElement, text: string) => {
  const target = Array.from(host.querySelectorAll<HTMLElement>('div, span, button'))
    .filter(element => element.textContent?.trim() === text)
    .pop()
  expect(target, `clickable element with text "${text}"`).toBeDefined()
  act(() => {
    target!.dispatchEvent(new MouseEvent('click', { bubbles: true }))
  })
}

const click = (element: HTMLElement) => {
  act(() => {
    element.dispatchEvent(new MouseEvent('click', { bubbles: true }))
  })
}

const setInputValue = (input: HTMLInputElement | HTMLTextAreaElement, value: string) => {
  const prototype = input instanceof HTMLTextAreaElement ? HTMLTextAreaElement.prototype : HTMLInputElement.prototype
  Object.getOwnPropertyDescriptor(prototype, 'value')!.set!.call(input, value)
  input.dispatchEvent(new Event('input', { bubbles: true }))
}

/** Selects DemoWB, then its master worktree, then its PLC device — the navigator's cascade. */
const selectDevice = async (host: HTMLElement) => {
  clickText(host, 'DemoWB')
  await act(async () => {})
  clickText(host, 'master')
  await act(async () => {})
  click(host.querySelector<HTMLElement>('[data-device-target="dev1"]')!)
  await act(async () => {})
}

const addTaskDialog = () => document.body.querySelector<HTMLElement>('[data-slot="dialog-content"]')

beforeEach(() => {
  // clearAllMocks keeps implementations, so each test restates the defaults it depends on.
  vi.clearAllMocks()
  vi.mocked(api.listGraphWorktreeTasks).mockResolvedValue([task])
  vi.mocked(api.getEngineeringTaskDetail).mockResolvedValue({
    task, sessions: [], commits: [], sourceObjects: [], svnRevisions: [],
  } as api.EngineeringTaskDetail)
})

afterEach(() => {
  for (const root of mountedRoots.splice(0)) act(() => root.unmount())
  document.body.innerHTML = ''
})

describe('MainStudio task creation from the navigator TASKS section', () => {
  it('opens the add-task dialog from the section header with the selected device preselected', async () => {
    const { host } = render(<MainStudio />)
    await act(async () => {})
    await selectDevice(host)

    // AC-005: the header action opens creation with that device preselected. Selecting the device puts
    // the main area in the device workspace, which is the case that used to swallow the click.
    click(host.querySelector<HTMLButtonElement>('button[aria-label="Create task for PLC_Demo"]')!)
    await act(async () => {})

    const dialog = addTaskDialog()
    expect(dialog, 'Add task dialog').not.toBeNull()
    expect(dialog!.textContent).toContain('Add task')
    expect(dialog!.querySelector<HTMLSelectElement>('select[aria-label="New task target"]')?.value).toBe('dev1')
    // Creating a task for the selected target must not move that selection, and the dialog is the
    // shell's own, so the main area keeps showing the device workspace behind it instead of being
    // navigated to the worktree's task surface.
    expect(host.querySelector('footer')?.textContent).toContain('PLC_Demo')
    expect(host.querySelector('[data-flexlayout-mock]')).not.toBeNull()
    expect(document.querySelector('[aria-label="Task display mode"]')).toBeNull()
  })

  it('opens the add-task dialog from the empty TASKS list affordance', async () => {
    vi.mocked(api.listGraphWorktreeTasks).mockResolvedValue([])

    const { host } = render(<MainStudio />)
    await act(async () => {})
    await selectDevice(host)

    const addRow = Array.from(host.querySelectorAll<HTMLButtonElement>('button'))
      .find(button => button.textContent?.trim() === 'Add task')
    expect(addRow, 'Add task row').toBeDefined()
    click(addRow!)
    await act(async () => {})

    const dialog = addTaskDialog()
    expect(dialog, 'Add task dialog').not.toBeNull()
    expect(dialog!.querySelector<HTMLSelectElement>('select[aria-label="New task target"]')?.value).toBe('dev1')
    expect(host.querySelector('footer')?.textContent).toContain('PLC_Demo')
  })

  it('opens the add-task dialog while a task detail is showing', async () => {
    const { host } = render(<MainStudio />)
    await act(async () => {})
    await selectDevice(host)

    // A task detail is the other thing the main area can be showing; the create action has to reach
    // its dialog from there too, not just from the device workspace.
    click(host.querySelector<HTMLButtonElement>('button[aria-label="Open task Inspect startup sequence"]')!)
    await act(async () => {})
    expect(host.textContent).toContain('Task fields')

    click(host.querySelector<HTMLButtonElement>('button[aria-label="Create task for PLC_Demo"]')!)
    await act(async () => {})

    const dialog = addTaskDialog()
    expect(dialog, 'Add task dialog').not.toBeNull()
    expect(dialog!.querySelector<HTMLSelectElement>('select[aria-label="New task target"]')?.value).toBe('dev1')
    // The detail the user was reading stays on screen behind the shell's dialog.
    expect(host.textContent).toContain('Task fields')
  })

  it('creates the task bound to the target the action was invoked from', async () => {
    const { host } = render(<MainStudio />)
    await act(async () => {})
    await selectDevice(host)

    click(host.querySelector<HTMLButtonElement>('button[aria-label="Create task for PLC_Demo"]')!)
    await act(async () => {})
    const dialog = addTaskDialog()!

    await act(async () => setInputValue(dialog.querySelector<HTMLInputElement>('input[aria-label="New task title"]')!, 'Add alarm handling'))
    await act(async () => setInputValue(dialog.querySelector<HTMLInputElement>('input[aria-label="New task goal"]')!, 'Add an alarm for overtemperature'))
    await act(async () => setInputValue(dialog.querySelector<HTMLTextAreaElement>('textarea[aria-label="New task expected result"]')!, 'PLC raises an alarm above the limit'))
    click(dialog.querySelector<HTMLButtonElement>('button[type="submit"]')!)
    await act(async () => {})

    expect(vi.mocked(api.createGraphWorktreeTask)).toHaveBeenCalledWith('wb1', 'wt1', {
      title: 'Add alarm handling',
      deviceId: 'dev1',
      type: 'feature',
      intent: 'Add an alarm for overtemperature',
      expectedResult: 'PLC raises an alarm above the limit',
    })
    // Closing hands the main area back to the device the task was created for.
    expect(addTaskDialog()).toBeNull()
    expect(host.querySelector('footer')?.textContent).toContain('PLC_Demo')
  })
})
