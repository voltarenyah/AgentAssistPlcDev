// @vitest-environment happy-dom
// Item 001: no user action opens the right dock by itself. Selecting a worktree
// and starting a conversation from the chat empty state both leave
// `shellLayout.rightOpen` exactly as the user left it, in either direction.
import React, { act } from 'react'
import { createRoot } from 'react-dom/client'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import * as api from '@/api/client'
import { clearDeviceMetadataMemory } from './deviceSnapshot'
import { SHELL_LAYOUT_STORAGE_KEY } from './shellLayout'
import MainStudio from './MainStudio'

globalThis.IS_REACT_ACT_ENVIRONMENT = true

const workbench: api.Workbench = {
  schemaVersion: '1.0',
  workbenchId: 'wb1',
  name: 'DemoWB',
  createdAt: '2026-07-30T00:00:00Z',
  rootPath: 'C:/wb',
  worktrees: [
    { worktreeId: 'wt1', name: 'master', branch: 'master', relativePath: 'worktrees/master' },
    { worktreeId: 'wt2', name: 'feature-x', branch: 'feature-x', relativePath: 'worktrees/feature-x' },
  ],
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

const session: api.ChatSessionData = {
  header: {
    sessionId: 's1',
    title: 'New chat',
    workbenchId: 'wb1',
    worktreeId: 'wt1',
    deviceId: 'dev1',
    createdAt: '2026-08-02T00:00:00Z',
    updatedAt: '2026-08-02T00:00:00Z',
  },
  messages: [],
  roundUsages: [],
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
    listGraphWorktreeTasks: vi.fn(async () => []),
    listProjectTasks: vi.fn(async () => []),
    getDeviceInfo: vi.fn(async () => snapshot),
    listDeviceSessions: vi.fn(async () => []),
    newChatSession: vi.fn(async () => session),
    loadChatSession: vi.fn(async () => session),
    getKeyStatus: vi.fn(async () => ({ configured: true })),
    getDeepSeekBalance: vi.fn(async () => ({ isAvailable: true, balances: [], fetchedAt: '2026-08-02T00:00:00.000Z' })),
    getSessions: vi.fn(async () => []),
  }
})

// happy-dom has no layout engine; swap in the lightweight FlexLayout stand-in.
vi.mock('flexlayout-react', async () => await import('@/test/flexLayoutMock'))

const render = (element: React.ReactNode) => {
  const host = document.createElement('div')
  document.body.appendChild(host)
  const root = createRoot(host)
  act(() => root.render(element))
  return { host, root }
}

const clickText = async (host: HTMLElement, text: string) => {
  const target = Array.from(host.querySelectorAll<HTMLElement>('div, span, button'))
    .filter(element => element.textContent?.trim() === text)
    .pop()
  expect(target, `clickable element with text "${text}"`).toBeDefined()
  await act(async () => {
    target!.dispatchEvent(new MouseEvent('click', { bubbles: true }))
  })
}

const dockState = (host: HTMLElement) =>
  host.querySelector('[data-dock="right"]')?.getAttribute('data-dock-state')

const toggleRightDock = async (host: HTMLElement) => {
  const toggle = host.querySelector<HTMLButtonElement>('[data-dock-toggle="right"]')
  expect(toggle).not.toBeNull()
  await act(async () => { toggle!.click() })
}

/**
 * The persisted layout — the state this item is actually about. It stays observable on a view that
 * renders no dock at all, which is what the retired AI sessions page left the device chat view as.
 */
const persistedRightOpen = () => {
  const raw = window.localStorage.getItem(SHELL_LAYOUT_STORAGE_KEY)
  return raw ? (JSON.parse(raw) as { rightOpen?: boolean }).rightOpen : undefined
}

/** Selects the device whose workspace hosts the chat empty state. */
const selectDevice = async (host: HTMLElement) => {
  await clickText(host, 'DemoWB')
  await clickText(host, 'master')
  await clickText(host, 'PLC_Demo')
}

/** Focuses the workspace's AI chat view, where the empty state lives. */
const focusChatView = async (host: HTMLElement) => {
  const chatTab = Array.from(host.querySelectorAll<HTMLElement>('[role="tab"]'))
    .find(tab => tab.textContent?.trim() === 'AI chat')
  expect(chatTab, 'AI chat tab').toBeDefined()
  await act(async () => { chatTab!.click() })
}

const startConversationFromEmptyState = async (host: HTMLElement) => {
  const create = host.querySelector<HTMLButtonElement>('button[aria-label="Create new chat session"]')
  expect(create, 'chat empty-state create button').not.toBeNull()
  await act(async () => { create!.click() })
}

afterEach(() => {
  document.body.innerHTML = ''
})

beforeEach(() => {
  vi.clearAllMocks()
  clearDeviceMetadataMemory()
  window.localStorage.clear()
})

describe('MainStudio right dock is never opened by an action', () => {
  it('leaves a collapsed right dock collapsed when another worktree is selected', async () => {
    const { host } = render(<MainStudio />)
    await act(async () => {})

    await clickText(host, 'DemoWB')
    await clickText(host, 'master')
    expect(dockState(host)).toBe('open')

    await toggleRightDock(host)
    expect(dockState(host)).toBe('closed')

    await clickText(host, 'feature-x')

    expect(dockState(host)).toBe('closed')
    expect(host.querySelector('button[aria-label="Show context dock"]')).not.toBeNull()
  })

  it('leaves an open right dock open when another worktree is selected', async () => {
    const { host } = render(<MainStudio />)
    await act(async () => {})

    await clickText(host, 'DemoWB')
    await clickText(host, 'master')
    expect(dockState(host)).toBe('open')

    await clickText(host, 'feature-x')

    expect(dockState(host)).toBe('open')
  })

  it('keeps a collapsed right dock collapsed when a conversation starts from the chat empty state', async () => {
    const { host } = render(<MainStudio />)
    await act(async () => {})

    await selectDevice(host)
    await toggleRightDock(host)
    expect(dockState(host)).toBe('closed')
    expect(persistedRightOpen()).toBe(false)

    await focusChatView(host)
    await startConversationFromEmptyState(host)

    expect(api.newChatSession).toHaveBeenCalledTimes(1)
    // The retired AI sessions page leaves a device on the chat view with no right dock at all, so
    // the state the user chose is asserted where it lives rather than on a rendered attribute: the
    // conversation started without reopening anything.
    expect(host.querySelector('[data-dock="right"]')).toBeNull()
    expect(persistedRightOpen()).toBe(false)
  })

  it('keeps an open right dock open when a conversation starts from the chat empty state', async () => {
    const { host } = render(<MainStudio />)
    await act(async () => {})

    await selectDevice(host)
    expect(dockState(host)).toBe('open')
    expect(persistedRightOpen()).toBe(true)

    await focusChatView(host)
    await startConversationFromEmptyState(host)

    expect(api.newChatSession).toHaveBeenCalledTimes(1)
    expect(host.querySelector('[data-dock="right"]')).toBeNull()
    expect(persistedRightOpen()).toBe(true)
  })
})
