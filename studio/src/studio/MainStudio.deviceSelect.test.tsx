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
  knowledge: { state: 'missing', updatedAt: null },
  blocks: [
    { id: 'b1', name: 'Main', number: 1, blockType: 'OB', programmingLanguage: 'LAD', groupPath: 'Area', relativePath: 'Blocks/Main [OB1].xml', modified: false },
  ],
  sourceObjectCount: 7,
  diagnostics: [],
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

afterEach(() => {
  document.body.innerHTML = ''
})

beforeEach(() => {
  vi.clearAllMocks()
})

describe('MainStudio device selection resilience', () => {
  it('collapses and reopens the left and right docks independently', async () => {
    const { host } = render(<MainStudio />)
    await act(async () => {})

    clickText(host, 'DemoWB')
    await act(async () => {})
    clickText(host, 'master')
    await act(async () => {})

    const leftToggle = host.querySelector<HTMLButtonElement>('[data-dock-toggle="left"]')
    const rightToggle = host.querySelector<HTMLButtonElement>('[data-dock-toggle="right"]')
    expect(leftToggle).not.toBeNull()
    expect(rightToggle).not.toBeNull()
    expect(host.querySelector('[data-dock="left"]')?.getAttribute('data-dock-state')).toBe('open')
    expect(host.querySelector('[data-dock="right"]')?.getAttribute('data-dock-state')).toBe('open')

    act(() => leftToggle?.click())
    expect(host.querySelector('[data-dock="left"]')?.getAttribute('data-dock-state')).toBe('closed')
    expect(host.querySelector('[data-dock="right"]')?.getAttribute('data-dock-state')).toBe('open')

    act(() => rightToggle?.click())
    expect(host.querySelector('[data-dock="right"]')?.getAttribute('data-dock-state')).toBe('closed')
    expect(host.querySelector('[data-status-bar]')).not.toBeNull()

    act(() => leftToggle?.click())
    expect(host.querySelector('[data-dock="left"]')?.getAttribute('data-dock-state')).toBe('open')
  })

  it('applies device selection instantly without waiting for the snapshot or engineering server', async () => {
    // Every backend call that is not pure identity hangs forever: selection must
    // still apply immediately (per-block snapshot work is background-only).
    vi.mocked(api.getSessions).mockImplementation(() => new Promise<api.SessionInfo[]>(() => {}))
    vi.mocked(api.getDeviceInfo).mockImplementation(() => new Promise<api.DeviceSnapshot>(() => {}))
    vi.mocked(api.listDeviceSessions).mockImplementation(() => new Promise<api.ChatSessionInfo[]>(() => {}))

    const { host } = render(<MainStudio />)
    await act(async () => {})

    clickText(host, 'DemoWB')
    await act(async () => {})
    clickText(host, 'master')
    await act(async () => {})

    expect(host.querySelector('footer')?.textContent).toContain('dev1')
    expect(host.querySelector('footer')?.textContent).not.toContain('no device')
    // While the snapshot is still loading, the brand-new bootstrap panel must not flash.
    expect(host.textContent).not.toContain('Generate PLC context')
  })

  it('fills in the device view when the snapshot arrives', async () => {
    // clearAllMocks keeps implementations — restore resolving defaults explicitly.
    vi.mocked(api.getSessions).mockResolvedValue([])
    vi.mocked(api.getDeviceInfo).mockResolvedValue(snapshot)
    vi.mocked(api.listDeviceSessions).mockResolvedValue([])

    const { host } = render(<MainStudio />)
    await act(async () => {})

    clickText(host, 'DemoWB')
    await act(async () => {})
    clickText(host, 'master')
    await act(async () => {})
    await act(async () => {})

    expect(host.querySelector('footer')?.textContent).toContain('PLC_Demo')
    expect(host.querySelector('footer')?.textContent).not.toContain('no device')
    const sourceObjectsMetric = Array.from(host.querySelectorAll('div'))
      .find(element => element.textContent === 'Source objects')
    expect(sourceObjectsMetric?.previousElementSibling?.textContent).toBe('7')
    expect(host.textContent).not.toContain('Touched overlays')
    // Established device (snapshot has blocks): no bootstrap panel.
    expect(host.textContent).not.toContain('Generate PLC context')
  })

  it('describes source editing without a modified overlay model', async () => {
    vi.mocked(api.getSessions).mockResolvedValue([])
    vi.mocked(api.getDeviceInfo).mockResolvedValue(snapshot)
    vi.mocked(api.listDeviceSessions).mockResolvedValue([])

    const { host } = render(<MainStudio />)
    await act(async () => {})
    clickText(host, 'DemoWB')
    await act(async () => {})
    clickText(host, 'master')
    await act(async () => {})
    await act(async () => {})

    const sourceTab = Array.from(host.querySelectorAll<HTMLButtonElement>('button'))
      .find(element => element.textContent?.trim() === 'PLC source')
    expect(sourceTab).toBeDefined()
    act(() => sourceTab!.click())

    expect(host.textContent?.toLowerCase()).not.toContain('overlay')
    expect(host.textContent).not.toContain('Modified sources')
  })

  it('starts up even when TIA session enumeration never responds', async () => {
    vi.mocked(api.getSessions).mockImplementation(() => new Promise<api.SessionInfo[]>(() => {}))

    const { host } = render(<MainStudio />)
    await act(async () => {})
    await act(async () => {})

    expect(host.textContent).toContain('DemoWB')
    expect(host.querySelector('[data-api-status]')?.textContent).toContain('API online')
  })
})
