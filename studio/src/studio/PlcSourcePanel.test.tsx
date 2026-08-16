// @vitest-environment happy-dom
import React, { act } from 'react'
import { createRoot } from 'react-dom/client'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
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
})
