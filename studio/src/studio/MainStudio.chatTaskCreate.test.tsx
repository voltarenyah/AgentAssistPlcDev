// @vitest-environment happy-dom
import React, { act } from 'react'
import { createRoot } from 'react-dom/client'
import { afterEach, beforeEach, expect, it, vi } from 'vitest'
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
  knowledge: { state: 'current', updatedAt: null },
  blocks: [],
  sourceObjectCount: 0,
  diagnostics: [],
}

const session: api.ChatSessionData = {
  header: {
    sessionId: 's1',
    title: 'New chat',
    workbenchId: 'wb1',
    worktreeId: 'wt1',
    deviceId: 'dev1',
    createdAt: '2026-08-01T00:00:00Z',
    updatedAt: '2026-08-01T00:00:00Z',
  },
  messages: [],
  roundUsages: [],
}

const settings: api.ChatSettings = {
  model: 'deepseek-v4-flash',
  thinkingEnabled: true,
  reasoningEffort: 'high',
  temperature: 1,
  topP: 1,
}

vi.mock('@/api/client', async importOriginal => {
  const actual = await importOriginal<typeof import('@/api/client')>()
  return {
    ...actual,
    listWorkbenches: vi.fn(async () => [workbench]),
    selectWorkbench: vi.fn(async () => ({})),
    selectWorktree: vi.fn(async () => ({})),
    listProjectTasks: vi.fn(async () => []),
    listGraphWorktreeTasks: vi.fn(async () => []),
    getWorktreeDetail: vi.fn(async () => ({
      worktreeId: 'wt1', workbenchId: 'wb1', name: 'master', branch: 'master',
      createdAt: '2026-08-01T00:00:00Z', baseCommit: null, engineeringProjectId: null,
      sourceProjectPath: null, deviceIds: ['dev1'], lastReconciliationCommit: null,
      purpose: null, owner: null, status: 'active', finishedUtc: null,
    })),
    listDevices: vi.fn(async () => [{ deviceId: 'dev1', plcName: 'PLC_Demo' }]),
    getDeviceInfo: vi.fn(async () => snapshot),
    listDeviceSessions: vi.fn(async () => []),
    getKeyStatus: vi.fn(async () => ({ configured: true })),
    getDeepSeekBalance: vi.fn(async () => ({ isAvailable: true, balances: [], fetchedAt: '2026-08-02T00:00:00.000Z' })),
    getSessions: vi.fn(async () => []),
    selectDevice: vi.fn(async () => ({})),
    newChatSession: vi.fn(async () => session),
    loadChatSession: vi.fn(async () => session),
    sendChatMessage: vi.fn(async () => {}),
    getChatSettings: vi.fn(async () => settings),
    saveChatSettings: vi.fn(async () => {}),
    getLogs: vi.fn(async () => [] as string[]),
    confirmTool: vi.fn(async () => true),
    getAppAssistantRuntimeState: vi.fn(async () => null),
    subscribeAppAssistantRuntime: vi.fn(() => () => {}),
    bootstrapAppAssistant: vi.fn(async () => []),
    chatAppAssistant: vi.fn(async () => []),
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

const clickAriaLabel = (host: HTMLElement, label: string) => {
  const target = host.querySelector<HTMLElement>(`[aria-label="${label}"]`)
  expect(target, `clickable element with aria-label "${label}"`).toBeDefined()
  act(() => {
    target!.dispatchEvent(new MouseEvent('click', { bubbles: true }))
  })
}

/** Selects the workbench, its worktree and its device, and opens a conversation on the device chat. */
const openDeviceChat = async () => {
  const { host, root } = render(<MainStudio />)
  await act(async () => {})
  clickText(host, 'DemoWB')
  await act(async () => {})
  clickText(host, 'master')
  await act(async () => {})
  const deviceRow = host.querySelector<HTMLElement>('[data-device-target="dev1"]')
  expect(deviceRow, 'navigator device row').not.toBeNull()
  act(() => {
    deviceRow!.dispatchEvent(new MouseEvent('click', { bubbles: true }))
  })
  await act(async () => {})
  await act(async () => {})
  clickText(host, 'AI chat')
  await act(async () => {})
  clickAriaLabel(host, 'Create new chat session')
  await act(async () => {})
  await act(async () => {})
  return { host, root }
}

const submitTurn = async (host: HTMLElement, message: string) => {
  const composer = host.querySelector<HTMLFormElement>('form[data-chat-composer="s1"]')!
  expect(composer, 'chat composer').not.toBeNull()
  const textarea = composer.querySelector<HTMLTextAreaElement>('textarea[name="message"]')!
  textarea.value = message
  await act(async () => {
    composer.dispatchEvent(new Event('submit', { bubbles: true, cancelable: true }))
  })
  await act(async () => {})
}

afterEach(() => {
  for (const root of mountedRoots.splice(0)) act(() => root.unmount())
  document.body.innerHTML = ''
})

beforeEach(() => {
  vi.clearAllMocks()
})

it('re-reads the worktree task list after a device-chat turn', async () => {
  const { host } = await openDeviceChat()

  // The knowledge agent can record a finding it established in the conversation as a new task
  // (create_task), so the list the navigator's TASKS section shows is re-read after every turn: an
  // approved task must appear without the user re-selecting the worktree.
  const created: api.EngineeringTask = {
    taskId: 'task-new', workbenchId: 'wb1', scope: 'worktree', worktreeId: 'wt1', deviceId: 'dev1',
    title: 'Door 202 opens without the safety gate closed', type: 'issue', status: 'todo', priority: 0,
    intent: 'Prevent the door from opening while the gate is open',
    expectedResult: 'Door 202 only opens when the gate reports closed',
    description: '## Evidence\n\nNetwork 3 has no interlock.',
    createdUtc: '2026-08-02T00:00:00Z', updatedUtc: '2026-08-02T00:00:00Z',
  }
  vi.mocked(api.listGraphWorktreeTasks).mockClear()
  vi.mocked(api.listGraphWorktreeTasks).mockResolvedValue([created])
  await submitTurn(host, 'Record this finding as a task.')

  expect(api.sendChatMessage).toHaveBeenCalled()
  expect(api.listGraphWorktreeTasks).toHaveBeenCalledWith('wb1', 'wt1')
  await act(async () => {})
  // The refreshed list is the one the navigator renders, so the created task is visible.
  expect(host.querySelector(`[aria-label="Open task ${created.title}"]`)).not.toBeNull()
})

it('re-reads the conversation list after a turn, so the task the conversation created lists it', async () => {
  // Before the turn the conversation is related to nothing, so the navigator shows it in the device's
  // task-less list.
  const taskless: api.ChatSessionInfo = {
    sessionId: 's1', title: 'New chat', projectName: null, workbenchId: 'wb1', worktreeId: 'wt1',
    deviceId: 'dev1', createdAt: '2026-08-01T00:00:00Z', updatedAt: '2026-08-01T00:00:00Z',
    messageCount: 1, turnCount: 0, firstUserMessage: null, taskId: null,
  }
  vi.mocked(api.listDeviceSessions).mockResolvedValue([taskless])
  const { host } = await openDeviceChat()
  expect(host.querySelector('[data-session-group="unbound"]')).not.toBeNull()

  // The turn's own create_task call relates the conversation to the task it created, with provenance
  // auto, so the list the row belongs to changes without the user binding anything (AC-010, AC-015).
  const created: api.EngineeringTask = {
    taskId: 'task-new', workbenchId: 'wb1', scope: 'worktree', worktreeId: 'wt1', deviceId: 'dev1',
    title: 'Door 202 opens without the safety gate closed', type: 'issue', status: 'todo', priority: 0,
    intent: 'Prevent the door from opening while the gate is open',
    expectedResult: 'Door 202 only opens when the gate reports closed',
    description: '## Evidence\n\nNetwork 3 has no interlock.',
    createdUtc: '2026-08-02T00:00:00Z', updatedUtc: '2026-08-02T00:00:00Z',
  }
  const related: api.ChatSessionInfo = {
    ...taskless, taskId: created.taskId, taskProvenance: 'auto',
    taskRelations: [{ taskId: created.taskId, edgeId: 'edge-auto', provenance: 'auto', isPrimary: true }],
  }
  vi.mocked(api.listGraphWorktreeTasks).mockResolvedValue([created])
  vi.mocked(api.listDeviceSessions).mockResolvedValue([related])
  vi.mocked(api.listDeviceSessions).mockClear()
  await submitTurn(host, 'Record this finding as a task.')

  // The relation belongs to the graph, so the conversation list is re-read after the turn: the
  // conversation has left the task-less list and is what the created task now lists.
  expect(api.listDeviceSessions).toHaveBeenCalledWith('wb1', 'wt1', 'dev1')
  expect(host.querySelector('[data-session-group="unbound"]')).toBeNull()

  clickAriaLabel(host, `Open task ${created.title}`)
  await act(async () => {})
  expect(host.querySelector('[data-session-group]')?.getAttribute('data-session-group')).toBe(created.taskId)
  expect(host.querySelector('[data-session="s1"]')).not.toBeNull()
})

it('keeps the chat turn alive when the task-list refresh fails', async () => {
  const { host } = await openDeviceChat()

  // A failed auxiliary read must not surface as a turn error or drop the conversation.
  vi.mocked(api.listGraphWorktreeTasks).mockRejectedValue(new Error('task list unavailable'))
  await submitTurn(host, 'Summarize this device.')

  expect(api.sendChatMessage).toHaveBeenCalled()
  expect(host.querySelector('[data-session-pane="s1"]')).not.toBeNull()
})
