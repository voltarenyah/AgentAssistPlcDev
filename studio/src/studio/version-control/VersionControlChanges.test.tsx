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

const snapshot = { revision: 3, commitsSince: 2, hardwareDiffers: false }

const render = async (entries: VersionControlSourceEntry[], snapshotOverride = snapshot, compareSignal = 0, untrackablePendingSavepoint = false) => {
  const host = document.createElement('div')
  document.body.appendChild(host)
  const root = createRoot(host)
  await act(async () => {
    root.render(
      <VersionControlChanges
        workbenchId="wb-1"
        worktreeId="wt-1"
        branch="master"
        entries={entries}
        compareSignal={compareSignal}
        snapshot={snapshotOverride}
        untrackablePendingSavepoint={untrackablePendingSavepoint}
      />,
    )
  })
  return { host, root }
}

const click = async (element: Element) => {
  await act(async () => element.dispatchEvent(new MouseEvent('click', { bubbles: true })))
}

const type = async (element: HTMLTextAreaElement | HTMLInputElement, value: string) => {
  await act(async () => {
    const prototype = element instanceof HTMLTextAreaElement ? HTMLTextAreaElement.prototype : HTMLInputElement.prototype
    Object.getOwnPropertyDescriptor(prototype, 'value')!.set!.call(element, value)
    element.dispatchEvent(new Event('input', { bubbles: true }))
  })
}

afterEach(() => {
  document.body.innerHTML = ''
  vi.restoreAllMocks()
})

describe('VersionControlChanges', () => {
  it('groups PLC objects into collapsible folders and selects rows on click', async () => {
    const { host } = await render([
      entry({ filePath: 'devices/PLC_1/source/Blocks/A.xml', objectName: 'A' }),
      entry({ filePath: 'devices/PLC_1/source/Blocks/B.xml', objectName: 'B', state: 'Unauthorized', authorizedOnMaster: false }),
      entry({ plcName: 'PLC_2', deviceId: 'dev-2', category: 'Tags', objectName: 'Inputs', filePath: 'devices/PLC_2/source/Tags/Inputs.xml' }),
    ])

    expect(host.textContent).toContain('PLC_1 · Block')
    expect(host.textContent).toContain('PLC_2 · Tags')

    const row = host.querySelector('[data-testid="plc-source-row"]')!
    expect(row.getAttribute('data-selected')).toBe('false')
    await click(row)
    expect(host.querySelector('[data-testid="plc-source-row"]')!.getAttribute('data-selected')).toBe('true')
    expect(host.textContent).toContain('Commit selected (1)')
  })

  it('commits exactly the selected paths with the typed message', async () => {
    const commit = vi.spyOn(api, 'commitVcPaths').mockResolvedValue({
      sha: 'abc',
      message: 'change A',
      files: ['devices/PLC_1/source/Blocks/A.xml'],
    })
    const { host } = await render([
      entry({ filePath: 'devices/PLC_1/source/Blocks/A.xml', objectName: 'A' }),
      entry({ filePath: 'devices/PLC_1/source/Blocks/B.xml', objectName: 'B' }),
    ])

    const commitButton = host.querySelector('[data-testid="vc-commit-selected"]') as HTMLButtonElement
    expect(commitButton.disabled).toBe(true)

    await click(host.querySelectorAll('[data-testid="plc-source-row"]')[0])
    await type(host.querySelector('textarea[aria-label="Commit message"]')!, 'change A')
    await click(host.querySelector('[data-testid="vc-commit-selected"]')!)

    expect(commit).toHaveBeenCalledWith('wb-1', 'wt-1', ['devices/PLC_1/source/Blocks/A.xml'], 'change A', false)
  })

  it('commits all changes through the split-button menu', async () => {
    const commit = vi.spyOn(api, 'commitVcPaths').mockResolvedValue({
      sha: 'abc',
      message: 'all',
      files: ['devices/PLC_1/source/Blocks/A.xml', 'devices/PLC_1/source/Blocks/B.xml'],
    })
    const { host } = await render([
      entry({ filePath: 'devices/PLC_1/source/Blocks/A.xml', objectName: 'A' }),
      entry({ filePath: 'devices/PLC_1/source/Blocks/B.xml', objectName: 'B' }),
    ])

    await type(host.querySelector('textarea[aria-label="Commit message"]')!, 'all')
    await click(host.querySelector('button[aria-label="Commit options"]')!)
    await click(host.querySelector('[data-testid="vc-commit-all"]')!)

    expect(commit).toHaveBeenCalledWith('wb-1', 'wt-1', [
      'devices/PLC_1/source/Blocks/A.xml',
      'devices/PLC_1/source/Blocks/B.xml',
    ], 'all', false)
  })

  it('shows the clean-state hero when there are no changes', async () => {
    const { host } = await render([])
    expect(host.querySelector('[data-testid="vc-changes-empty"]')?.textContent).toContain('No changes on this branch')
    expect(host.querySelector('textarea')).toBeNull()
  })

  it('shows the snapshot area with revision, drift, and hardware badges', async () => {
    const { host } = await render([entry()], { revision: 3, commitsSince: 2, hardwareDiffers: true })

    expect(host.querySelector('[data-testid="vc-snapshot-revision"]')?.textContent).toBe('r3')
    expect(host.querySelector('[data-testid="vc-snapshot-drift"]')?.textContent).toContain('2 commits since')
    expect(host.querySelector('[data-testid="vc-hardware-differs"]')?.textContent).toContain('hardware different')
  })

  it('creates a TIA snapshot only with a description', async () => {
    const create = vi.spyOn(api, 'createSvnSavepoint').mockResolvedValue({ sha: 'deadbeefcafe', message: 'before IP change', files: [] })
    const { host } = await render([], { revision: null, commitsSince: null, hardwareDiffers: false })

    expect(host.textContent).toContain('No savepoint yet')
    const button = host.querySelector('[data-testid="vc-create-snapshot"]') as HTMLButtonElement
    expect(button.disabled).toBe(true)

    await type(host.querySelector('input[aria-label="Description for TIA snapshot"]')!, 'before IP change')
    await click(host.querySelector('[data-testid="vc-create-snapshot"]')!)

    expect(create).toHaveBeenCalledWith('wb-1', 'wt-1', 'before IP change')
  })

  it('runs the TIA comparison inline when signalled and lists the detected files', async () => {
    const compare = vi.spyOn(api, 'compareMasterWithTia').mockResolvedValue({
      comparisonId: 'comparison-1',
      masterSha: 'master-1',
      fastGatePassed: false,
      state: 'Different',
      liveChecksums: {},
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
    })
    vi.spyOn(api, 'getWorktreeEngineeringState').mockRejectedValue(new Error('no state'))
    const { host } = await render([entry()], snapshot, 1)

    expect(compare).toHaveBeenCalledTimes(1)
    expect(host.querySelector('[data-testid="vc-compare-result"]')).toBeTruthy()
    expect(host.textContent).toContain('PLC_1 · Main')
    expect(host.textContent).not.toContain('TIA differs from master')
    const commitControls = host.querySelector('[data-testid="vc-commit-controls"]')!
    const compareResult = host.querySelector('[data-testid="vc-compare-result"]')!
    expect(commitControls.compareDocumentPosition(compareResult) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy()
  })

  it('uses the global commit action to overwrite selected local sources from TIA and commit them', async () => {
    vi.spyOn(api, 'compareMasterWithTia')
      .mockResolvedValueOnce({
        comparisonId: 'comparison-1',
        masterSha: 'master-1',
        fastGatePassed: false,
        state: 'Different',
        liveChecksums: {},
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
      })
      .mockResolvedValueOnce({
        comparisonId: 'comparison-2',
        masterSha: 'commit-2',
        fastGatePassed: true,
        state: 'Consistent',
        liveChecksums: {},
        differences: [],
      })
    vi.spyOn(api, 'getWorktreeEngineeringState').mockRejectedValue(new Error('no state'))
    const accept = vi.spyOn(api, 'acceptTiaSynchronization').mockResolvedValue({
      comparisonId: 'comparison-1',
      pendingPaths: [],
      commitSha: 'commit-2',
    })
    const commit = vi.spyOn(api, 'commitVcPaths')
    const { host } = await render([], snapshot, 1)

    await click(host.querySelector('[data-testid="vc-compare-result"] input[type="checkbox"]')!)
    await type(host.querySelector('textarea[aria-label="Commit message"]')!, 'Accept Main from TIA')
    expect(host.textContent).toContain('Commit selected (1)')
    await click(host.querySelector('[data-testid="vc-commit-selected"]')!)

    expect(accept).toHaveBeenCalledWith(
      'wb-1',
      'comparison-1',
      ['devices/PLC_1/source/Blocks/Main.xml'],
      'Accept Main from TIA',
    )
    expect(commit).not.toHaveBeenCalled()
    expect(host.querySelector('[data-testid="vc-compare-result"] input[type="checkbox"]')).toBeNull()
    expect(host.querySelector('[data-testid="vc-clean-state"]')).toBeTruthy()
  })

  it('keeps the commit button disabled with a message but zero selected paths until the untrackable checkbox is ticked', async () => {
    const { host } = await render([entry()])

    const commitButton = host.querySelector('[data-testid="vc-commit-selected"]') as HTMLButtonElement
    expect(commitButton.disabled).toBe(true)

    await type(host.querySelector('textarea[aria-label="Commit message"]')!, 'TIA-only change')
    expect(commitButton.disabled).toBe(true)

    const checkbox = host.querySelector('[data-testid="vc-untrackable-change"]') as HTMLInputElement
    expect(checkbox.checked).toBe(false)
    await click(checkbox)

    expect(checkbox.checked).toBe(true)
    expect((host.querySelector('[data-testid="vc-commit-selected"]') as HTMLButtonElement).disabled).toBe(false)
  })

  it('commits an untrackable message-only change with empty paths and clears message and checkbox on success', async () => {
    const commit = vi.spyOn(api, 'commitVcPaths').mockResolvedValue({
      sha: 'deadbeefcafe',
      message: 'TIA-only change',
      files: [],
    })
    const { host } = await render([entry()])

    await type(host.querySelector('textarea[aria-label="Commit message"]')!, 'TIA-only change')
    await click(host.querySelector('[data-testid="vc-untrackable-change"]')!)
    await click(host.querySelector('[data-testid="vc-commit-selected"]')!)

    expect(commit).toHaveBeenCalledWith('wb-1', 'wt-1', [], 'TIA-only change', true)
    expect((host.querySelector('textarea[aria-label="Commit message"]') as HTMLTextAreaElement).value).toBe('')
    expect((host.querySelector('[data-testid="vc-untrackable-change"]') as HTMLInputElement).checked).toBe(false)
  })

  it('warns in the snapshot area when an untrackable change has no savepoint coverage', async () => {
    const { host } = await render([entry()], snapshot, 0, true)

    const warning = host.querySelector('[data-testid="vc-untrackable-savepoint-warning"]')!
    expect(warning.textContent).toContain('not covered by any SVN savepoint')
  })

  it('hides the savepoint warning when every untrackable change is covered', async () => {
    const { host } = await render([entry()])

    expect(host.querySelector('[data-testid="vc-untrackable-savepoint-warning"]')).toBeNull()
  })
})
