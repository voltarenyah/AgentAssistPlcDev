// @vitest-environment happy-dom
import React, { act } from 'react'
import { createRoot } from 'react-dom/client'
import { afterEach, describe, expect, it, vi } from 'vitest'
import * as api from '@/api/client'
import VersionControlChanges, { type VersionControlSourceEntry } from './VersionControlChanges'

globalThis.IS_REACT_ACT_ENVIRONMENT = true

const entry = (overrides: Partial<VersionControlSourceEntry> = {}): VersionControlSourceEntry => ({
  filePath: 'devices/PLC_1/source/Blocks/Main.xml',
  deviceId: 'dev-1',
  plcName: 'PLC_1',
  category: 'Block',
  objectName: 'Main',
  state: 'Modified',
  authorizedOnMaster: true,
  ...overrides,
})

const render = async (entries: VersionControlSourceEntry[], onSelectionChange = vi.fn()) => {
  const host = document.createElement('div')
  document.body.appendChild(host)
  const root = createRoot(host)
  await act(async () => {
    root.render(
      <VersionControlChanges
        workbenchId="wb-1"
        worktreeId="wt-1"
        entries={entries}
        onSelectionChange={onSelectionChange}
      />,
    )
  })
  return { host, root, onSelectionChange }
}

const click = async (element: Element) => {
  await act(async () => element.dispatchEvent(new MouseEvent('click', { bubbles: true })))
}

const input = async (element: HTMLInputElement, value: string) => {
  await act(async () => {
    element.value = value
    element.dispatchEvent(new Event('input', { bubbles: true }))
    element.dispatchEvent(new Event('change', { bubbles: true }))
  })
}

afterEach(() => {
  document.body.innerHTML = ''
  vi.restoreAllMocks()
})

describe('VersionControlChanges', () => {
  it('groups PLC objects and commits exactly the selected paths', async () => {
    const commit = vi.spyOn(api, 'commitVcPaths').mockResolvedValue({
      sha: 'abc',
      message: 'change A',
      files: ['devices/PLC_1/source/Blocks/A.xml'],
    })
    const { host } = await render([
      entry({ filePath: 'devices/PLC_1/source/Blocks/A.xml', objectName: 'A' }),
      entry({ filePath: 'devices/PLC_1/source/Blocks/B.xml', objectName: 'B' }),
      entry({ plcName: 'PLC_2', deviceId: 'dev-2', category: 'Tags', objectName: 'Inputs', filePath: 'devices/PLC_2/source/Tags/Inputs.xml' }),
    ])

    expect(host.textContent).toContain('PLC_1 · Block')
    expect(host.textContent).toContain('PLC_2 · Tags')

    await click(host.querySelector('input[aria-label="Select A"]')!)
    await input(host.querySelector('input[aria-label="Commit message"]')!, 'change A')
    await click(host.querySelector('button[aria-label="Commit selected (1)"]')!)

    expect(commit).toHaveBeenCalledWith(
      'wb-1',
      'wt-1',
      ['devices/PLC_1/source/Blocks/A.xml'],
      'change A',
    )
  })

  it('selects all objects in a group; direct master edits stay selectable and labeled', async () => {
    const { host } = await render([
      entry({ objectName: 'A', filePath: 'devices/PLC_1/source/Blocks/A.xml' }),
      entry({ objectName: 'B', filePath: 'devices/PLC_1/source/Blocks/B.xml', state: 'Unauthorized', authorizedOnMaster: false }),
    ])

    const groupSelect = host.querySelector('input[aria-label="Select all PLC_1 Block objects"]')!
    await click(groupSelect)

    expect((host.querySelector('input[aria-label="Select A"]') as HTMLInputElement).checked).toBe(true)
    // Direct master edits are committable (policy relaxed); the label is informational only.
    expect((host.querySelector('input[aria-label="Select B"]') as HTMLInputElement).checked).toBe(true)
    expect(host.textContent).toContain('Direct master edit')
    expect(host.textContent).not.toMatch(/Stage|Unstage|Restore/)
  })

  it('reports row selection separately from commit selection', async () => {
    const onSelectionChange = vi.fn()
    const item = entry()
    const { host } = await render([item], onSelectionChange)

    await click(host.querySelector('[data-testid="plc-source-row"]')!)

    expect(onSelectionChange).toHaveBeenCalledWith(item)
  })
})
