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
  exportedSourceRoot: 'C:/wb/exported',
  modifiedSourceRoot: 'C:/wb/modified',
  knowledgeDbPath: 'C:/wb/plc-knowledge.db',
  sourceProjectPath: 'D:/proj.ap17',
  device: null,
  knowledge: { state: 'missing', updatedAt: null },
  blocks: [
    { id: 'b1', name: 'Main', number: 1, blockType: 'OB', programmingLanguage: 'LAD', groupPath: 'Area', relativePath: 'Blocks/Main [OB1].xml', modified: false },
  ],
  overlayCount: 0,
  diagnostics: [],
}

vi.mock('@/api/client', async importOriginal => {
  const actual = await importOriginal<typeof import('@/api/client')>()
  return {
    ...actual,
    listWorkbenches: vi.fn(async () => [workbench]),
    listDevices: vi.fn(async () => ['dev1']),
    getDeviceInfo: vi.fn(async () => snapshot),
    listDeviceSessions: vi.fn(async () => []),
    getKeyStatus: vi.fn(async () => ({ configured: true })),
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

    expect(host.querySelector('header')?.textContent).toContain('dev1')
    expect(host.querySelector('header')?.textContent).not.toContain('no device')
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

    expect(host.querySelector('header')?.textContent).toContain('PLC_Demo')
    expect(host.querySelector('header')?.textContent).not.toContain('no device')
    // Established device (snapshot has blocks): no bootstrap panel.
    expect(host.textContent).not.toContain('Generate PLC context')
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
