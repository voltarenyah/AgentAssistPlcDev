// @vitest-environment happy-dom
import React, { act } from 'react'
import { createRoot } from 'react-dom/client'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import * as api from '@/api/client'
import type { SourceObjectInfo } from '@/api/client'
import PlcSourcePanel from './PlcSourcePanel'
import type { DeviceViewState } from './deviceSnapshot'

const toastMock = vi.hoisted(() => ({
  success: vi.fn(),
}))

vi.mock('sonner', () => ({ toast: toastMock }))
vi.mock('@/api/client', () => ({
  openSourceInTia: vi.fn(),
  compareSourceWithTia: vi.fn(),
  getGraphEntityDetail: vi.fn(),
}))

globalThis.IS_REACT_ACT_ENVIRONMENT = true

const sourceObject = (index: number): SourceObjectInfo => ({
  id: `source-${index}`,
  name: `Block ${index}`,
  number: index,
  category: 'FB',
  programmingLanguage: 'SCL',
  groupPath: null,
  relativePath: `Blocks/Block${index} [FB${index}].xml`,
  contentHash: null,
  isKnowHowProtected: null,
  modifiedDate: null,
  status: null,
})

const render = async (element: React.ReactNode) => {
  const host = document.createElement('div')
  document.body.appendChild(host)
  const root = createRoot(host)
  await act(async () => root.render(element))
  return { host, root }
}

const deviceView = {
  sourceObjects: Array.from({ length: 201 }, (_, index) => sourceObject(index)),
  blocks: [],
  diagnostics: [],
} as unknown as DeviceViewState

describe('PlcSourcePanel', () => {
  beforeEach(() => {
    toastMock.success.mockReset()
    vi.mocked(api.getGraphEntityDetail).mockReset()
  })

  afterEach(() => {
    document.body.innerHTML = ''
  })

  it('limits large source lists and notifies that the view is truncated', async () => {
    const { host } = await render(
      <PlcSourcePanel
        workbenchId="wb1"
        worktreeId="wt1"
        deviceId="dev1"
        deviceView={deviceView}
        onChatWithAgent={vi.fn()}
        onSnapshotReload={vi.fn()}
        onInspectObject={vi.fn()}
        onInspectUsage={vi.fn()}
      />,
    )

    expect(host.querySelectorAll('[data-testid="plc-source-row"]')).toHaveLength(200)
    expect(host.textContent).toContain('Showing the first 200 of 201 matching source objects')

    const input = host.querySelector<HTMLInputElement>('input[placeholder="Filter by name, path, or type…"]')!
    const setter = Object.getOwnPropertyDescriptor(window.HTMLInputElement.prototype, 'value')!.set!
    await act(async () => {
      setter.call(input, 'Block 200')
      input.dispatchEvent(new Event('input', { bubbles: true }))
    })

    expect(host.querySelectorAll('[data-testid="plc-source-row"]')).toHaveLength(1)
    expect(host.textContent).toContain('Block 200')
  })

  it('keeps a selected source object outside the cap visible and loads its task links', async () => {
    vi.mocked(api.getGraphEntityDetail).mockResolvedValue({
      kind: 'sourceObject',
      id: 'source-200',
      workbenchId: 'wb1',
      worktreeId: 'wt1',
      tasks: [{ id: 'task-200', edgeId: 'edge-200', provenance: 'manual', isPrimary: true }],
      commits: [{ id: 'commit-200', edgeId: 'commit-edge-200', provenance: 'source-evidence', isPrimary: true }],
    })

    const onNavigateTask = vi.fn()
    const onNavigateEntity = vi.fn()

    const { host } = await render(
      <PlcSourcePanel
        workbenchId="wb1"
        worktreeId="wt1"
        deviceId="dev1"
        deviceView={deviceView}
        onChatWithAgent={vi.fn()}
        onSnapshotReload={vi.fn()}
        onNavigateTask={onNavigateTask}
        onNavigateEntity={onNavigateEntity}
        selectedTraceabilityTarget={{ kind: 'sourceObject', id: 'source-200' }}
      />,
    )

    await act(async () => {})

    const selectedRow = Array.from(host.querySelectorAll<HTMLElement>('[data-testid="plc-source-row"]'))
      .find(row => row.textContent?.includes('Block 200'))
    expect(selectedRow).toBeTruthy()
    expect(selectedRow?.textContent).toContain('Blocks/Block200 [FB200].xml')
    expect(selectedRow?.textContent).toContain('task-200')
    expect(selectedRow?.textContent).toContain('manual')
    expect(selectedRow?.textContent).toContain('commit-200')
    expect(selectedRow?.textContent).toContain('source-evidence')
    const openCommit = host.querySelector<HTMLButtonElement>('button[aria-label="Open commit commit-200"]')
    expect(openCommit).toBeTruthy()
    await act(async () => openCommit?.click())
    expect(onNavigateEntity).toHaveBeenCalledWith('gitCommit', 'commit-200')
    expect(api.getGraphEntityDetail).toHaveBeenCalledWith('wb1', 'sourceObject', 'source-200')
  })
})
