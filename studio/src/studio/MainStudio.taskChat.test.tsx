// @vitest-environment happy-dom
import React, { act } from 'react'
import { createRoot } from 'react-dom/client'
import { afterEach, beforeEach, expect, it, vi } from 'vitest'
import * as api from '@/api/client'
import MainStudio from './MainStudio'

globalThis.IS_REACT_ACT_ENVIRONMENT = true

const workbench: api.Workbench = {
  schemaVersion: '1.0', workbenchId: 'wb1', name: 'DemoWB', createdAt: '2026-07-30T00:00:00Z',
  rootPath: 'C:/wb', worktrees: [{ worktreeId: 'wt1', name: 'master', branch: 'master', relativePath: 'worktrees/master' }],
}
const task: api.EngineeringTask = {
  taskId: 'task1', workbenchId: 'wb1', scope: 'worktree', worktreeId: 'wt1', deviceId: 'dev1',
  title: 'Inspect startup sequence', type: 'feature', status: 'todo', priority: 0,
  intent: 'Find startup fault', expectedResult: 'Validated fix', description: null,
  createdUtc: '2026-08-02T00:00:00Z', updatedUtc: '2026-08-02T00:00:00Z',
}
const session: api.ChatSessionData = {
  header: {
    sessionId: 's1', title: 'New chat', workbenchId: 'wb1', worktreeId: 'wt1',
    deviceId: 'dev1', taskId: 'task1', createdAt: '2026-08-02T00:00:00Z', updatedAt: '2026-08-02T00:00:00Z',
  },
  messages: [], roundUsages: [],
}

vi.mock('@/api/client', async importOriginal => {
  const actual = await importOriginal<typeof import('@/api/client')>()
  return {
    ...actual,
    listWorkbenches: vi.fn(async () => [workbench]),
    selectWorkbench: vi.fn(async () => ({})),
    selectWorktree: vi.fn(async () => ({})),
    selectDevice: vi.fn(async () => ({})),
    listDevices: vi.fn(async () => [{ deviceId: 'dev1', plcName: 'PLC_Demo' }]),
    listGraphWorktreeTasks: vi.fn(async () => [task]),
    listProjectTasks: vi.fn(async () => []),
    getWorktreeDetail: vi.fn(async () => ({
      worktreeId: 'wt1', workbenchId: 'wb1', name: 'master', branch: 'master',
      createdAt: '2026-08-02T00:00:00Z', baseCommit: null, engineeringProjectId: null,
      sourceProjectPath: null, deviceIds: ['dev1'], lastReconciliationCommit: null,
      purpose: null, owner: null, status: 'ongoing', finishedUtc: null,
    } satisfies api.WorktreeDetail)),
    listDeviceSessions: vi.fn(async () => [{
      sessionId: 's1', title: 'New chat', projectName: null, workbenchId: 'wb1', worktreeId: 'wt1', deviceId: 'dev1',
      createdAt: '2026-08-02T00:00:00Z', updatedAt: '2026-08-02T00:00:00Z', messageCount: 1, turnCount: 1,
      firstUserMessage: 'Find startup fault', taskId: 'task1', taskProvenance: 'default',
    }]),
    getEngineeringTaskDetail: vi.fn(async () => ({
      task, sessions: [{ id: 's1', edgeId: 'edge-1', provenance: 'default', isPrimary: true }],
      commits: [], sourceObjects: [], svnRevisions: [],
    } satisfies api.EngineeringTaskDetail)),
    loadDeviceChatSession: vi.fn(async () => session),
    getDeviceInfo: vi.fn(),
    newChatSession: vi.fn(async () => session),
    loadChatSession: vi.fn(async () => session),
    sendChatMessage: vi.fn(async () => {}),
    getKeyStatus: vi.fn(async () => ({ configured: true })),
    getDeepSeekBalance: vi.fn(async () => ({ isAvailable: true, balances: [], fetchedAt: '2026-08-02T00:00:00Z' })),
    getSessions: vi.fn(async () => []),
  }
})
vi.mock('flexlayout-react', async () => await import('@/test/flexLayoutMock'))

beforeEach(() => vi.clearAllMocks())
afterEach(() => { document.body.innerHTML = '' })

it('starts task chat from its bound device without selecting or snapshotting the device', async () => {
  const host = document.createElement('div')
  document.body.appendChild(host)
  const root = createRoot(host)
  await act(async () => root.render(<MainStudio />))

  const clickText = async (text: string) => {
    const target = Array.from(host.querySelectorAll<HTMLElement>('div, span, button'))
      .filter(element => element.textContent?.trim() === text).pop()
    expect(target).toBeDefined()
    await act(async () => target!.click())
  }
  await clickText('DemoWB')
  await clickText('master')
  await clickText('Tasks')
  const start = host.querySelector<HTMLButtonElement>('[aria-label="New chat for Inspect startup sequence"]')
  expect(start).not.toBeNull()
  vi.mocked(api.getDeviceInfo).mockClear()
  vi.mocked(api.selectDevice).mockClear()
  await act(async () => start!.click())

  expect(api.newChatSession).toHaveBeenCalledWith(undefined, 'task1')
  expect(api.selectDevice).not.toHaveBeenCalled()
  expect(api.getDeviceInfo).not.toHaveBeenCalled()
  expect(host.textContent).not.toContain('Select a device before opening chat.')

  const input = host.querySelector<HTMLTextAreaElement>('[data-session-pane="s1"] textarea')
  expect(input).not.toBeNull()
  await act(async () => {
    const setValue = Object.getOwnPropertyDescriptor(HTMLTextAreaElement.prototype, 'value')?.set
    setValue?.call(input, 'Check startup fault')
    input!.dispatchEvent(new InputEvent('input', { bubbles: true, inputType: 'insertText' }))
  })
  await act(async () => {
    host.querySelector<HTMLFormElement>('form[data-chat-composer="s1"]')?.dispatchEvent(
      new Event('submit', { bubbles: true, cancelable: true }),
    )
  })
  expect(api.sendChatMessage).toHaveBeenCalled()
  expect(api.selectDevice).toHaveBeenCalledWith('wb1', 'wt1', 'dev1')
  expect(api.getDeviceInfo).not.toHaveBeenCalled()
  await act(async () => root.unmount())
})

it('opens a task-bound session from its task card without expanding the context dock', async () => {
  const host = document.createElement('div')
  document.body.appendChild(host)
  const root = createRoot(host)
  await act(async () => root.render(<MainStudio />))

  const clickText = async (text: string) => {
    const target = Array.from(host.querySelectorAll<HTMLElement>('div, span, button'))
      .filter(element => element.textContent?.trim() === text).pop()
    expect(target).toBeDefined()
    await act(async () => target!.click())
  }
  await clickText('DemoWB')
  await clickText('master')
  await clickText('Tasks')
  const hideDock = host.querySelector<HTMLButtonElement>('button[aria-label="Hide context dock"]')
  expect(hideDock).not.toBeNull()
  await act(async () => hideDock!.click())
  const showSessions = host.querySelector<HTMLButtonElement>('button[aria-label="Show 1 sessions for Inspect startup sequence"]')
  expect(showSessions).not.toBeNull()
  await act(async () => showSessions!.click())
  const open = host.querySelector<HTMLButtonElement>('button[aria-label="Open conversation New chat"]')
  expect(open).toBeDefined()
  vi.mocked(api.selectDevice).mockClear()
  vi.mocked(api.getDeviceInfo).mockClear()
  await act(async () => open!.click())

  expect(api.loadDeviceChatSession).toHaveBeenCalledWith('wb1', 'wt1', 'dev1', 's1')
  expect(api.selectDevice).not.toHaveBeenCalled()
  expect(api.getDeviceInfo).not.toHaveBeenCalled()
  expect(host.querySelector('[data-session-pane="s1"]')).not.toBeNull()
  expect(host.querySelector('button[aria-label="Show context dock"]')).not.toBeNull()
  await act(async () => root.unmount())
})

it('opens a navigator conversation without dropping the selected device or its task', async () => {
  const host = document.createElement('div')
  document.body.appendChild(host)
  const root = createRoot(host)
  await act(async () => root.render(<MainStudio />))

  const clickText = async (text: string) => {
    const target = Array.from(host.querySelectorAll<HTMLElement>('div, span, button'))
      .filter(element => element.textContent?.trim() === text).pop()
    expect(target).toBeDefined()
    await act(async () => target!.click())
  }
  const sections = () => Array.from(host.querySelectorAll('[data-navigator-section]'))
    .map(node => node.getAttribute('data-navigator-section'))

  await clickText('DemoWB')
  await clickText('master')
  await clickText('PLC_Demo')

  // The only conversation is bound to the task, so with no task selected there is no list to show.
  expect(sections()).not.toContain('sessions')

  const taskRow = host.querySelector<HTMLButtonElement>('button[aria-label="Open task Inspect startup sequence"]')
  expect(taskRow).not.toBeNull()
  await act(async () => taskRow!.click())
  expect(sections()).toContain('sessions')
  expect(host.querySelector('[data-session="s1"]')).not.toBeNull()

  const open = host.querySelector<HTMLButtonElement>('[data-session-open="s1"]')
  expect(open).not.toBeNull()
  vi.mocked(api.selectWorktree).mockClear()
  vi.mocked(api.loadDeviceChatSession).mockClear()
  await act(async () => open!.click())

  // The conversation is what changed: the navigator keeps the device, the task and the section.
  expect(api.loadDeviceChatSession).toHaveBeenCalledWith('wb1', 'wt1', 'dev1', 's1')
  expect(host.querySelector('[data-device-target="dev1"]')?.getAttribute('aria-current')).toBe('true')
  expect(sections()).toEqual(['projects', 'worktree', 'device', 'tasks', 'sessions'])
  expect(host.querySelector('[data-task-selected="true"]')?.textContent).toContain('Inspect startup sequence')
  expect(host.querySelector('[data-session="s1"]')).not.toBeNull()
  expect(host.querySelector('[data-session-pane="s1"]')).not.toBeNull()
  await act(async () => root.unmount())
})
