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
    { id: 'b1', name: 'Main', number: 1, blockType: 'OB', programmingLanguage: 'LAD', groupPath: 'Area', relativePath: 'Blocks/Main [OB1].xml', modified: false },
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

vi.mock('@/api/client', async importOriginal => {
  const actual = await importOriginal<typeof import('@/api/client')>()
  return {
    ...actual,
    listWorkbenches: vi.fn(async () => [workbench]),
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
  }
})

const render = (element: React.ReactNode) => {
  const host = document.createElement('div')
  document.body.appendChild(host)
  const root = createRoot(host)
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
  document.body.innerHTML = ''
})

beforeEach(() => {
  vi.clearAllMocks()
})

describe('MainStudio chat destructive-tool confirmation', () => {
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
    clickText(host, 'PLC_Demo')
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
