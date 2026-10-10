// @vitest-environment happy-dom
// Item 001: no user action opens the right dock by itself. Selecting a worktree
// and starting a conversation from the chat empty state both leave the column
// exactly as the user left it, in either direction. ADR-0015 later made the column
// exist on every surface, so the same guarantee is now also observable on the view
// itself: `data-dock-state` is the column's visibility, and the rail is always
// inside it.
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
    // The rail's badge is the shell's own read of the worktree's status, so it has to be answered even
    // when the changes page has never been opened.
    getWorktreeVcStatus: vi.fn(async () => ({ branch: 'master', entries: [] })),
    getHardwareConfiguration: vi.fn(async () => ({
      state: 'ok',
      projectAmlPath: 'C:/wb/hardware/project.aml',
      exportedAt: '2026-10-07T10:39:45Z',
      devices: [{
        id: 'd1',
        name: 'S7-1500/ET200MP station_1',
        kind: 'Device',
        typeIdentifier: null,
        properties: [],
        ioRanges: [],
      }],
      tags: [],
    })),
    getHardwareBom: vi.fn(async () => ({ state: 'ok', exportedAt: '2026-10-07T10:39:45Z', items: [] })),
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

const pageState = (host: HTMLElement) =>
  host.querySelector('[data-dock="right"]')?.getAttribute('data-page-state')

const railItem = (host: HTMLElement, page: 'properties' | 'changes' | 'history') =>
  host.querySelector<HTMLElement>(`[data-testid="right-dock-rail-${page}"]`)

const toggleRightDock = async (host: HTMLElement) => {
  const toggle = host.querySelector<HTMLButtonElement>('[data-dock-toggle="right"]')
  expect(toggle).not.toBeNull()
  await act(async () => { toggle!.click() })
}

/** The persisted layout — the state this item is actually about. */
const persistedRightColumn = () => {
  const raw = window.localStorage.getItem(SHELL_LAYOUT_STORAGE_KEY)
  return raw ? (JSON.parse(raw) as { rightColumnOpen?: boolean }).rightColumnOpen : undefined
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
    expect(persistedRightColumn()).toBe(false)

    await focusChatView(host)
    await startConversationFromEmptyState(host)

    expect(api.newChatSession).toHaveBeenCalledTimes(1)
    // The column is rendered on every surface now, so the state the user chose is asserted on it
    // rather than on its absence: starting a conversation reopened nothing (ADR-0015).
    expect(host.querySelector('[data-dock="right"]')).not.toBeNull()
    expect(dockState(host)).toBe('closed')
    expect(persistedRightColumn()).toBe(false)
  })

  it('keeps an open right dock open when a conversation starts from the chat empty state', async () => {
    const { host } = render(<MainStudio />)
    await act(async () => {})

    await selectDevice(host)
    expect(dockState(host)).toBe('open')
    expect(persistedRightColumn()).toBe(true)
    // A device selection opens the properties page: the surface the device page showed before the
    // rail existed is now the page the rail opens by default.
    expect(railItem(host, 'properties')?.getAttribute('aria-selected')).toBe('true')

    await focusChatView(host)
    await startConversationFromEmptyState(host)

    expect(api.newChatSession).toHaveBeenCalledTimes(1)
    expect(host.querySelector('[data-dock="right"]')).not.toBeNull()
    expect(dockState(host)).toBe('open')
    expect(pageState(host)).toBe('open')
    expect(persistedRightColumn()).toBe(true)
  })

  it('opens, switches and collapses a page from the rail', async () => {
    const { host } = render(<MainStudio />)
    await act(async () => {})

    await clickText(host, 'DemoWB')
    await clickText(host, 'master')
    await act(async () => {})
    // The worktree landing page opens its working tree, which is what it showed before the rail.
    expect(railItem(host, 'changes')?.getAttribute('aria-selected')).toBe('true')
    expect(pageState(host)).toBe('open')

    await act(async () => { railItem(host, 'history')!.click() })
    expect(railItem(host, 'history')?.getAttribute('aria-selected')).toBe('true')

    // Clicking the open page's own item collapses the page and leaves the rail marked.
    await act(async () => { railItem(host, 'history')!.click() })
    expect(pageState(host)).toBe('collapsed')
    expect(railItem(host, 'history')?.getAttribute('aria-selected')).toBe('true')
    expect(host.querySelector<HTMLElement>('[data-dock="right"]')?.style.width).toBe('44px')

    await act(async () => { railItem(host, 'history')!.click() })
    expect(pageState(host)).toBe('open')
  })

  it('offers a labelled expand handle while the page is collapsed', async () => {
    const { host } = render(<MainStudio />)
    await act(async () => {})

    await clickText(host, 'DemoWB')
    await clickText(host, 'master')
    await act(async () => {})
    expect(host.querySelector('[data-testid="right-dock-rail-expand"]')).toBeNull()

    // Collapsing the page leaves the rail alone in the column; the handle is the visible way back.
    await act(async () => { railItem(host, 'changes')!.click() })
    expect(pageState(host)).toBe('collapsed')
    const expand = host.querySelector<HTMLButtonElement>('[data-testid="right-dock-rail-expand"]')
    expect(expand).not.toBeNull()
    expect(expand?.getAttribute('aria-label')).toBe('Expand Changes')

    await act(async () => { expand!.click() })
    expect(pageState(host)).toBe('open')
    expect(host.querySelector('[data-testid="right-dock-rail-expand"]')).toBeNull()
  })

  it('badges the rail and the page header from its own status read, before any page is opened', async () => {
    vi.mocked(api.getWorktreeVcStatus).mockResolvedValueOnce({
      branch: 'master',
      entries: [
        { filePath: 'devices/PLC_1/source/Blocks/Main.xml', state: 'Modified' },
        { filePath: 'devices/PLC_1/source/DB/Data.xml', state: 'Modified' },
        { filePath: 'hardware/staging/project.aml', state: 'Modified' },
      ],
    } as api.VcStatusResult)

    const { host } = render(<MainStudio />)
    await act(async () => {})
    await clickText(host, 'DemoWB')
    await clickText(host, 'master')
    await act(async () => {})

    // Two source objects: the staging path is not one, and the changes page has never been opened.
    expect(host.querySelector('[data-testid="right-dock-rail-changes-badge"]')?.textContent).toBe('2')
    expect(host.querySelector('[data-testid="right-dock-page-chip"]')?.textContent).toBe('2 uncommitted')
  })

  it('shows no badge on a clean worktree', async () => {
    const { host } = render(<MainStudio />)
    await act(async () => {})
    await clickText(host, 'DemoWB')
    await clickText(host, 'master')
    await act(async () => {})

    expect(host.querySelector('[data-testid="right-dock-rail-changes-badge"]')).toBeNull()
    expect(host.querySelector('[data-testid="right-dock-page-chip"]')).toBeNull()
  })

  it('describes the selected hardware node on every hardware page', async () => {
    const { host } = render(<MainStudio />)
    await act(async () => {})
    await clickText(host, 'DemoWB')
    await clickText(host, 'master')
    await clickText(host, 'Hardware configuration')
    await act(async () => {})

    const propertiesPage = () => host.querySelector('[data-testid="right-dock-page-properties"]')?.textContent ?? ''
    expect(propertiesPage()).toContain('S7-1500/ET200MP station_1')

    // The BOM page shares the same node selection, so the properties page keeps describing it instead
    // of falling back to a hint that contradicts what the workspace shows.
    const bomTab = Array.from(host.querySelectorAll<HTMLElement>('button')).find(button => button.textContent?.trim() === 'BOM list')
    expect(bomTab, 'BOM list tab').toBeDefined()
    await act(async () => { bomTab!.click() })
    await act(async () => {})

    expect(propertiesPage()).toContain('S7-1500/ET200MP station_1')
    expect(propertiesPage()).not.toContain('Select a device, a hardware object, or a knowledge node')
  })
})
