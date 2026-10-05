// @vitest-environment happy-dom
import React, { act } from 'react'
import { createRoot } from 'react-dom/client'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import * as api from '@/api/client'
import MainStudio from './MainStudio'

globalThis.IS_REACT_ACT_ENVIRONMENT = true

/**
 * Item 005, device conversation: the agent's `stage_task_source_object` call parks on the shared
 * AgentSandbox card, and approving it is what makes the approved objects appear in the task page's
 * Source objects section. The server-side write itself is covered by
 * tests/ApiHost.Tests/TaskSourceStagingToolTests.cs — no model and no TIA run here.
 */

const workbench: api.Workbench = {
  schemaVersion: '1.0',
  workbenchId: 'wb1',
  name: 'DemoWB',
  createdAt: '2026-07-30T00:00:00Z',
  rootPath: 'C:/wb',
  worktrees: [{ worktreeId: 'wt1', name: 'master', branch: 'master', relativePath: 'worktrees/master' }],
}

const task: api.EngineeringTask = {
  taskId: 'task1', workbenchId: 'wb1', scope: 'worktree', worktreeId: 'wt1', deviceId: 'dev1',
  title: 'Inspect startup sequence', type: 'feature', status: 'todo', priority: 0,
  intent: 'Find startup fault', expectedResult: 'Validated fix', description: null,
  createdUtc: '2026-08-02T00:00:00Z', updatedUtc: '2026-08-02T00:00:00Z',
}

const stage: api.TaskSourceStage = {
  taskId: 'task1',
  sourceObjectId: 'dev1:block-main',
  deviceId: 'dev1',
  baselineEvidenceJson: '{ "id": "block-main" }',
  stagedUtc: '2026-09-30T00:00:00Z',
}

const snapshot: api.DeviceSnapshot = {
  workbenchId: 'wb1', worktreeId: 'wt1', deviceId: 'dev1', plcName: 'PLC_Demo', engineeringIdentity: 'PLC_Demo',
  sourceRoot: 'C:/wb/source', knowledgeDbPath: 'C:/wb/plc-knowledge.db', sourceProjectPath: 'D:/proj.ap17',
  device: null,
  knowledge: { state: 'current', updatedAt: null },
  blocks: [
    { id: 'b1', name: 'Main', number: 1, blockType: 'OB', programmingLanguage: 'LAD', groupPath: 'Area', relativePath: 'Blocks/Main [OB1].xml', modified: true },
  ],
  sourceObjects: [
    {
      id: 'block-main', name: 'Main', number: 1, category: 'OB', programmingLanguage: 'LAD',
      groupPath: 'Program blocks', relativePath: 'Blocks/Main.xml', contentHash: null,
      isKnowHowProtected: null, modifiedDate: null, status: null,
    },
  ],
  sourceObjectCount: 1,
  diagnostics: [],
}

const session: api.ChatSessionData = {
  header: {
    sessionId: 's1', title: 'New chat', workbenchId: 'wb1', worktreeId: 'wt1', deviceId: 'dev1',
    taskId: 'task1', createdAt: '2026-08-01T00:00:00Z', updatedAt: '2026-08-01T00:00:00Z',
  },
  messages: [],
  roundUsages: [],
}

const settings: api.ChatSettings = {
  model: 'deepseek-v4-flash', thinkingEnabled: true, reasoningEffort: 'high', temperature: 1, topP: 1,
}

const assistantRuntime: api.AppAssistantRuntimeSnapshot = {
  schemaVersion: 1, workbenchId: 'wb1', workbenchRevision: 1,
  focus: { worktreeId: null, deviceId: null }, worktrees: [], availableActions: [],
  operation: { status: 'idle', operationId: null, kind: null, message: null },
  observedAt: '2026-08-02T00:00:00Z',
}

/** The server writes the stage only when the approval is resolved, so the list changes after it. */
let approved = false

const stageArguments = JSON.stringify({
  objects: [{ sourceObjectId: 'dev1:block-main' }],
})

vi.mock('@/api/client', async importOriginal => {
  const actual = await importOriginal<typeof import('@/api/client')>()
  return {
    ...actual,
    listWorkbenches: vi.fn(async () => [workbench]),
    selectWorkbench: vi.fn(async () => ({})),
    selectWorktree: vi.fn(async () => ({})),
    selectDevice: vi.fn(async () => ({})),
    getBranchStartPoints: vi.fn(async () => []),
    getSandboxRoots: vi.fn(async () => ({ roots: ['C:/wb'] })),
    listProjectTasks: vi.fn(async () => []),
    listGraphWorktreeTasks: vi.fn(async () => [task]),
    getWorktreeDetail: vi.fn(async () => ({
      worktreeId: 'wt1', workbenchId: 'wb1', name: 'master', branch: 'master',
      createdAt: '2026-08-01T00:00:00Z', baseCommit: null, engineeringProjectId: null,
      sourceProjectPath: null, deviceIds: ['dev1'], lastReconciliationCommit: null,
      purpose: null, owner: null, status: 'active', finishedUtc: null,
    })),
    listDevices: vi.fn(async () => [{ deviceId: 'dev1', plcName: 'PLC_Demo' }]),
    getDeviceInfo: vi.fn(async () => snapshot),
    listDeviceSourceObjects: vi.fn(async () => snapshot.sourceObjects),
    listDeviceSessions: vi.fn(async () => []),
    getKeyStatus: vi.fn(async () => ({ configured: true })),
    getDeepSeekBalance: vi.fn(async () => ({ isAvailable: true, balances: [], fetchedAt: '2026-08-02T00:00:00.000Z' })),
    getSessions: vi.fn(async () => []),
    newChatSession: vi.fn(async () => session),
    loadChatSession: vi.fn(async () => session),
    sendChatMessage: vi.fn(async () => {}),
    getChatSettings: vi.fn(async () => settings),
    saveChatSettings: vi.fn(async () => {}),
    getLogs: vi.fn(async () => [] as string[]),
    confirmTool: vi.fn(async () => true),
    getEngineeringTaskDetail: vi.fn(async () => ({
      task, sessions: [], commits: [], sourceObjects: [], svnRevisions: [],
    } satisfies api.EngineeringTaskDetail)),
    listTaskSourceStages: vi.fn(async () => (approved ? [stage] : [])),
    listWorktreeSourceStages: vi.fn(async () => []),
    setActiveWorktreeTask: vi.fn(async () => ({})),
    getAppAssistantRuntimeState: vi.fn(async () => assistantRuntime),
    subscribeAppAssistantRuntime: vi.fn(() => () => {}),
    bootstrapAppAssistant: vi.fn(async () => [
      { kind: 'state', data: { runtimeSnapshot: assistantRuntime, sessionId: 'assistant-session' } },
      { kind: 'answer', data: { answer: 'Ready.' } },
    ]),
    chatAppAssistant: vi.fn(async () => []),
  }
})

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
  // not.toBeNull(): null also satisfies toBeDefined(), which turned a missing element into a
  // dispatchEvent TypeError instead of naming the label that is absent.
  expect(target, `clickable element with aria-label "${label}"`).not.toBeNull()
  act(() => {
    target!.dispatchEvent(new MouseEvent('click', { bubbles: true }))
  })
}

afterEach(() => {
  for (const root of mountedRoots.splice(0)) act(() => root.unmount())
  document.body.innerHTML = ''
})

beforeEach(() => {
  vi.clearAllMocks()
  approved = false
})

/** Selects workbench, worktree and device, then starts a device-conversation turn that parks on the
 * staging approval card. */
const startStageApproval = async () => {
  vi.mocked(api.sendChatMessage).mockImplementation(() => new Promise<void>(() => {}))
  vi.mocked(api.getLogs).mockResolvedValue([
    JSON.stringify({
      kind: 'confirmation', id: 'c-stage', requester: 's1',
      toolName: 'stage_task_source_object', arguments: stageArguments,
    }),
  ])

  const { host, root } = render(<MainStudio />)
  await act(async () => {})
  clickText(host, 'DemoWB')
  await act(async () => {})
  clickText(host, 'master')
  await act(async () => {})
  const deviceButton = Array.from(host.querySelectorAll<HTMLButtonElement>('button'))
    .find(button => button.textContent?.includes('PLC_Demo —'))
  expect(deviceButton).toBeDefined()
  act(() => deviceButton!.click())
  await act(async () => {})
  await act(async () => {})

  clickText(host, 'AI chat')
  await act(async () => {})
  // The retired AI sessions page used to hold the only "New session" button; the chat surface's own
  // empty state is the entry point now, exactly as items 001 and 006 left it.
  clickAriaLabel(host, 'Create new chat session')
  await act(async () => {})
  await act(async () => {})

  const composer = host.querySelector<HTMLFormElement>('form[data-chat-composer="s1"]')
  const textarea = composer!.querySelector<HTMLTextAreaElement>('textarea[name="message"]')!
  textarea.value = 'add Main to this task'
  act(() => {
    composer!.dispatchEvent(new Event('submit', { bubbles: true, cancelable: true }))
  })
  await act(async () => {})
  await act(async () => {})
  return { host, root }
}

describe('MainStudio agent stage approval', () => {
  it('shows the approval card for the stage call with the objects being approved', async () => {
    const { host } = await startStageApproval()

    const card = host.querySelector('[data-confirmation="c-stage"]')
    expect(card, 'approval card for stage_task_source_object').not.toBeNull()
    expect(card!.textContent).toContain('stage_task_source_object')
    // The card shows the full arguments, so the user sees exactly which object is being staged.
    expect(card!.textContent).toContain('dev1:block-main')
  })

  it('shows the approved object in the task page Source objects section after the approval', async () => {
    const { host } = await startStageApproval()
    expect(host.querySelector('[aria-label="Source objects"]')).toBeNull()

    act(() => {
      vi.mocked(api.confirmTool).mockImplementation(async () => {
        approved = true
        return true
      })
    })
    clickText(host, 'Allow once')
    await act(async () => {})

    expect(api.confirmTool).toHaveBeenCalledWith('c-stage', 'allowOnce')

    // The approved objects appear on the task page, which re-reads the stages it owns.
    clickAriaLabel(host, 'Open task Inspect startup sequence')
    await act(async () => {})
    await act(async () => {})

    const section = host.querySelector<HTMLElement>('[aria-label="Source objects"]')
    expect(section, 'task page Source objects section').not.toBeNull()
    expect(section!.textContent).toContain('Main')
    expect(api.listTaskSourceStages).toHaveBeenCalled()
  })
})
