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
  blocks: [
    { id: 'b1', name: 'Main', number: 1, blockType: 'OB', programmingLanguage: 'LAD', groupPath: 'Area', relativePath: 'Blocks/Main [OB1].xml', modified: true },
  ],
  sourceObjectCount: 1,
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

const assistantRuntime: api.AppAssistantRuntimeSnapshot = {
  schemaVersion: 1,
  workbenchId: 'wb1',
  workbenchRevision: 1,
  focus: { worktreeId: null, deviceId: null },
  worktrees: [],
  availableActions: [],
  operation: { status: 'idle', operationId: null, kind: null, message: null },
  observedAt: '2026-08-02T00:00:00Z',
}

vi.mock('@/api/client', async importOriginal => {
  const actual = await importOriginal<typeof import('@/api/client')>()
  return {
    ...actual,
    listWorkbenches: vi.fn(async () => [workbench]),
    selectWorkbench: vi.fn(async () => ({})),
    selectWorktree: vi.fn(async () => ({})),
    getBranchStartPoints: vi.fn(async () => []),
    getSandboxRoots: vi.fn(async () => ({ roots: ['C:/wb'] })),
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
    getAppAssistantRuntimeState: vi.fn(async () => assistantRuntime),
    subscribeAppAssistantRuntime: vi.fn(() => () => {}),
    bootstrapAppAssistant: vi.fn(async () => [
      { kind: 'state', data: { runtimeSnapshot: assistantRuntime, sessionId: 'assistant-session' } },
      { kind: 'answer', data: { answer: 'Ready.' } },
    ]),
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

afterEach(() => {
  for (const root of mountedRoots.splice(0)) act(() => root.unmount())
  document.body.innerHTML = ''
})

beforeEach(() => {
  vi.clearAllMocks()
})

describe('MainStudio chat destructive-tool confirmation', () => {
  it('keeps the compact Assistant available on Settings', async () => {
    const { host } = render(<MainStudio />)
    await act(async () => {})
    const composer = host.querySelector('[data-app-assistant]')
    expect(composer?.closest('header')).not.toBeNull()
    expect(host.querySelector('[data-app-assistant-panel]')?.getAttribute('aria-hidden')).toBe('true')

    clickAriaLabel(host, 'Settings')
    expect(host.querySelector('[data-app-assistant]')).toBe(composer)
    expect(host.querySelector('[data-app-assistant-panel]')?.getAttribute('aria-hidden')).toBe('true')
    clickAriaLabel(host, 'Open Workbench Assistant')
    expect(host.querySelector('[data-app-assistant-panel]')?.getAttribute('aria-hidden')).toBe('false')
  })

  it('opens the Workbench Assistant from home before a project is selected', async () => {
    vi.mocked(api.bootstrapAppAssistant).mockResolvedValueOnce([
      { kind: 'state', data: { runtimeSnapshot: null, sessionId: 'assistant-session' } },
      { kind: 'answer', data: { answer: 'Which project would you like to use?' } },
    ])
    const { host } = render(<MainStudio />)
    await act(async () => {})

    clickAriaLabel(host, 'Open Workbench Assistant')
    await act(async () => {})
    expect(host.querySelector('[data-app-assistant-panel]')).not.toBeNull()
    expect(host.textContent).toContain('Which project would you like to use?')
    expect(host.querySelector<HTMLInputElement>('input[aria-label="Workbench Assistant message"]')?.disabled).toBe(false)
  })

  it('shows a Workbench Assistant confirmation while its turn waits', async () => {
    vi.mocked(api.chatAppAssistant).mockImplementation(() => new Promise(() => {}))
    vi.mocked(api.getLogs).mockResolvedValue([
      JSON.stringify({ kind: 'confirmation', id: 'other-session', requester: 'device-session', toolName: 'import_block', arguments: '{}' }),
      JSON.stringify({ kind: 'confirmation', id: 'assistant-confirm', requester: 'assistant-session', toolName: 'vc_restore', arguments: '{}' }),
    ])

    const { host } = render(<MainStudio />)
    await act(async () => {})
    clickText(host, 'DemoWB')
    await act(async () => {})
    if (host.querySelector('[data-app-assistant-panel]')?.getAttribute('aria-hidden') === 'true') {
      clickAriaLabel(host, 'Open Workbench Assistant')
    }
    await act(async () => {})

    const input = host.querySelector<HTMLInputElement>('input[aria-label="Workbench Assistant message"]')!
    act(() => {
      const setter = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value')!.set!
      setter.call(input, 'Restore the previous version.')
      input.dispatchEvent(new Event('input', { bubbles: true }))
    })
    await act(async () => host.querySelector<HTMLButtonElement>('button[aria-label="Send assistant message"]')!.click())
    await act(async () => {})

    const card = host.querySelector('[data-app-assistant-confirmation="assistant-confirm"]')
    expect(card).not.toBeNull()
    const approve = card!.querySelector<HTMLButtonElement>('button')!
    expect(approve.disabled).toBe(false)
    await act(async () => approve.click())
    expect(api.confirmTool).toHaveBeenCalledWith('assistant-confirm', 'allowOnce')
  })

  it('shows the pending confirmation card while a turn waits and posts the decision', async () => {
    // The turn stays in-flight (chatBusy) while the server parks on the sandbox
    // confirmation; the /api/logs entry must surface as an approve/deny card.
    vi.mocked(api.sendChatMessage).mockImplementation(() => new Promise<void>(() => {}))
    vi.mocked(api.getLogs).mockResolvedValue([
      'plain log line',
      JSON.stringify({ kind: 'confirmation', id: 'c1', requester: 's1', toolName: 'import_block', arguments: '{"xmlFilePath":".../Blocks/Main [OB1].xml"}' }),
    ])

    const { host } = render(<MainStudio />)
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
    clickAriaLabel(host, 'New session')
    await act(async () => {})
    await act(async () => {})

    const composer = host.querySelector<HTMLFormElement>('form[data-chat-composer="s1"]')
    const textarea = composer!.querySelector<HTMLTextAreaElement>('textarea[name="message"]')!
    textarea.value = 'import it'
    act(() => {
      composer!.dispatchEvent(new Event('submit', { bubbles: true, cancelable: true }))
    })
    await act(async () => {})
    await act(async () => {})

    const card = host.querySelector('[data-confirmation="c1"]')
    expect(card, 'confirmation card for import_block').toBeDefined()
    expect(card!.textContent).toContain('import_block')

    clickText(host, 'Allow once')
    await act(async () => {})

    expect(vi.mocked(api.confirmTool)).toHaveBeenCalledWith('c1', 'allowOnce')
    expect(host.querySelector('[data-confirmation="c1"]')).toBeNull()
    // A resolved id must not resurface on later polls.
    await act(async () => { await new Promise(resolve => setTimeout(resolve, 1100)) })
    expect(host.querySelector('[data-confirmation="c1"]')).toBeNull()
  })
})
