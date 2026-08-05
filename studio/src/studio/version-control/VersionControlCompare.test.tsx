// @vitest-environment happy-dom
import React, { act } from 'react'
import { createRoot } from 'react-dom/client'
import { afterEach, describe, expect, it, vi } from 'vitest'
import * as api from '@/api/client'
import VersionControlCompare from './VersionControlCompare'

globalThis.IS_REACT_ACT_ENVIRONMENT = true

const comparison = (overrides: Partial<api.WorkbenchConsistencyResult> = {}): api.WorkbenchConsistencyResult => ({
  comparisonId: 'comparison-1',
  masterSha: 'master-1',
  fastGatePassed: false,
  state: 'Different',
  liveChecksums: { 'dev-1': 'checksum-2' },
  differences: [{
    deviceId: 'dev-1',
    plcName: 'PLC_1',
    relativePath: 'devices/PLC_1/source/Blocks/Main.xml',
    identity: 'Main',
    kind: 'Changed',
    masterFingerprint: 'old',
    tiaFingerprint: 'new',
    supported: true,
  }],
  ...overrides,
})

const render = async (onCommitted?: () => void) => {
  const host = document.createElement('div')
  document.body.appendChild(host)
  const root = createRoot(host)
  await act(async () => root.render(<VersionControlCompare workbenchId="wb-1" worktreeId="wt-1" branch="master" onCommitted={onCommitted} />))
  return { host, root }
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
  vi.restoreAllMocks()
  document.body.innerHTML = ''
})

describe('VersionControlCompare', () => {
  it('requires a title and auto-commits selected TIA changes', async () => {
    const compare = vi.spyOn(api, 'compareMasterWithTia')
      .mockResolvedValueOnce(comparison())
      .mockResolvedValueOnce(comparison({ differences: [], state: 'Consistent' }))
    const accept = vi.spyOn(api, 'acceptTiaSynchronization').mockResolvedValue({
      comparisonId: 'comparison-1',
      pendingPaths: [],
      commitSha: 'commit-2',
    })
    const { host } = await render()

    await click(host.querySelector('button[aria-label="Compare with TIA"]')!)
    await click(host.querySelector('input[type="checkbox"]')!)

    const acceptButton = host.querySelector('button[aria-label="Accept selected TIA changes"]') as HTMLButtonElement
    expect(acceptButton.disabled).toBe(true)
    await input(host.querySelector('input[aria-label="TIA commit title"]')!, 'Accept Main from TIA')
    expect(acceptButton.disabled).toBe(false)
    await click(acceptButton)

    expect(accept).toHaveBeenCalledWith(
      'wb-1',
      'comparison-1',
      ['devices/PLC_1/source/Blocks/Main.xml'],
      'Accept Main from TIA',
    )
    expect(compare).toHaveBeenCalledTimes(2)
    expect(host.textContent).toContain('Committed commit-2')
  })

  it('notifies the parent after an accepted TIA commit so history can refresh', async () => {
    vi.spyOn(api, 'compareMasterWithTia')
      .mockResolvedValueOnce(comparison())
      .mockResolvedValueOnce(comparison({ differences: [], state: 'Consistent' }))
    vi.spyOn(api, 'acceptTiaSynchronization').mockResolvedValue({
      comparisonId: 'comparison-1',
      pendingPaths: [],
      commitSha: 'commit-2',
    })
    const onCommitted = vi.fn()
    const { host } = await render(onCommitted)

    await click(host.querySelector('button[aria-label="Compare with TIA"]')!)
    await click(host.querySelector('input[type="checkbox"]')!)
    await input(host.querySelector('input[aria-label="TIA commit title"]')!, 'Accept Main from TIA')
    await click(host.querySelector('button[aria-label="Accept selected TIA changes"]')!)

    expect(onCommitted).toHaveBeenCalledOnce()
  })
})
