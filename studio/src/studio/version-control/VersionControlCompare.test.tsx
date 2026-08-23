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

const render = async (props: { signal?: number; commitMessage?: string; branch?: string; onCommitted?: () => void; onBeginOperation?: (kind: string, label: string) => string; onSelectionChanged?: (comparisonId: string | null, paths: string[]) => void } = {}) => {
  vi.spyOn(api, 'getWorktreeEngineeringState').mockResolvedValue({
    revision: {
      schemaVersion: 1,
      svn: { url: '^/native/main', revision: 3 },
      tia: { projectChecksum: 'dev-1:checksum-2' },
      safety: { fSignature: null },
      validation: { compileStatus: 'SUCCESS' },
    },
    svnUrl: null,
    baseSvnRevision: null,
    managedTiaProjectPath: null,
    tiaStorePath: 'C:/wb/worktrees/master/tia',
    pendingCommit: false,
  })
  const host = document.createElement('div')
  document.body.appendChild(host)
  const root = createRoot(host)
  await act(async () => root.render(
    <VersionControlCompare
      workbenchId="wb-1"
      worktreeId="wt-1"
      branch={props.branch ?? 'master'}
      signal={props.signal ?? 1}
      commitMessage={props.commitMessage ?? ''}
      onCommitted={props.onCommitted}
      onBeginOperation={props.onBeginOperation}
      onSelectionChanged={props.onSelectionChanged}
    />,
  ))
  return { host, root }
}

const click = async (element: Element) => {
  await act(async () => element.dispatchEvent(new MouseEvent('click', { bubbles: true })))
}

afterEach(() => {
  vi.restoreAllMocks()
  document.body.innerHTML = ''
})

describe('VersionControlCompare (inline)', () => {
  it('renders nothing until a compare is signalled', async () => {
    const compare = vi.spyOn(api, 'compareMasterWithTia').mockResolvedValue(comparison())
    const { host } = await render({ signal: 0 })

    expect(host.querySelector('[data-testid="vc-compare-result"]')).toBeNull()
    expect(compare).not.toHaveBeenCalled()
  })

  it('executes the comparison as soon as the signal arrives and lists differences', async () => {
    const compare = vi.spyOn(api, 'compareMasterWithTia').mockResolvedValue(comparison())
    const { host } = await render({ signal: 1 })

    expect(compare).toHaveBeenCalledTimes(1)
    expect(host.querySelector('[data-testid="vc-compare-result"]')).toBeTruthy()
    expect(host.textContent).toContain('PLC_1 · Main')
    expect(host.textContent).toContain('TIA differs from master')
  })

  it('asks before retrying a missing-checksum comparison with automatic compile and save', async () => {
    const compare = vi.spyOn(api, 'compareMasterWithTia')
      .mockRejectedValueOnce(new api.WorkbenchApiError(400, 'PLC_CHECKSUM_UNAVAILABLE', "TIA did not provide a compiled software checksum for PLC 'PLC_1'."))
      .mockResolvedValueOnce(comparison())
    const { host } = await render({ signal: 1 })

    expect(host.textContent).toContain('Compile and save')
    expect(host.querySelector('[aria-label="Compile and save in TIA, then compare"]')).toBeTruthy()

    await click(host.querySelector('[aria-label="Compile and save in TIA, then compare"]')!)

    expect(compare).toHaveBeenNthCalledWith(2, 'wb-1', undefined, true)
    expect(host.textContent).toContain('TIA differs from master')
  })

  it('does not offer a per-selection push-to-TIA action', async () => {
    vi.spyOn(api, 'compareMasterWithTia').mockResolvedValue(comparison())
    const { host } = await render({ signal: 1, commitMessage: 'Accept Main' })

    await click(host.querySelector('input[type="checkbox"]')!)

    expect(host.querySelector('[aria-label="Push selected local changes to TIA"]')).toBeNull()
  })

  it('reports checked TIA paths to the global commit flow without an individual accept button', async () => {
    vi.spyOn(api, 'compareMasterWithTia').mockResolvedValue(comparison())
    const onSelectionChanged = vi.fn()
    const { host } = await render({ signal: 1, onSelectionChanged })

    await click(host.querySelector('input[type="checkbox"]')!)

    expect(onSelectionChanged).toHaveBeenLastCalledWith(
      'comparison-1',
      ['devices/PLC_1/source/Blocks/Main.xml'],
    )
    expect(host.querySelector('[aria-label="Accept selected TIA changes"]')).toBeNull()
    expect(host.textContent).not.toContain('Accept 1 into local repo')
  })


  it('reports the full compare as a tracked operation when the host supports it', async () => {
    const compare = vi.spyOn(api, 'compareMasterWithTia').mockResolvedValue(comparison({ differences: [], state: 'Consistent' }))
    const onBeginOperation = vi.fn(() => 'op-42')
    await render({ signal: 1, onBeginOperation })

    expect(onBeginOperation).toHaveBeenCalledWith('compare-tia', expect.stringContaining('Comparing'))
    expect(compare).toHaveBeenCalledWith('wb-1', 'op-42')
  })

  it('hides the clean comparison when only the TIA checksum drifts', async () => {
    vi.spyOn(api, 'compareMasterWithTia').mockResolvedValue(comparison({
      differences: [],
      state: 'Different',
      liveChecksums: { 'dev-1': 'checksum-9' },
    }))
    const { host } = await render({ signal: 1 })

    expect(host.textContent).not.toContain('TIA matches master')
    expect(host.querySelector('[data-testid="vc-compare-result"]')).toBeNull()
  })

  it('reports TIA matches master when checksums match and no differences exist', async () => {
    vi.spyOn(api, 'compareMasterWithTia').mockResolvedValue(comparison({
      differences: [],
      state: 'Consistent',
      liveChecksums: { 'dev-1': 'checksum-2' },
    }))
    const { host } = await render({ signal: 1 })

    expect(host.textContent).not.toContain('TIA matches master')
    expect(host.querySelector('[data-testid="vc-compare-result"]')).toBeNull()
  })

  it('accepts project hardware changes with the commit message as title', async () => {
    const hardware = {
      state: 'changed' as const,
      rootPath: 'C:/wb/worktrees/master/hardware',
      stagingPath: 'C:/wb/worktrees/master/hardware/staging',
      artifacts: [{ scope: 'project' as const, deviceName: null, state: 'changed' as const }],
      message: 'Project hardware configuration differs from TIA.',
    }
    vi.spyOn(api, 'compareMasterWithTia')
      .mockResolvedValueOnce(comparison({ differences: [], state: 'Different', hardware }))
      .mockResolvedValueOnce(comparison({ differences: [], state: 'Consistent', hardware: { ...hardware, state: 'in-sync', artifacts: [{ ...hardware.artifacts[0], state: 'same' }] } }))
    const overwrite = vi.spyOn(api, 'overwriteHardwareConfiguration').mockResolvedValue({
      rootPath: hardware.rootPath,
      artifactCount: 1,
      commitSha: 'hardware-commit',
    })
    const { host } = await render({ signal: 1, commitMessage: 'Add safety relay' })

    expect(host.textContent).toContain('Project hardware differs from TIA')
    await click(host.querySelector('button[aria-label="Accept TIA hardware configuration"]')!)

    expect(overwrite).toHaveBeenCalledWith('wb-1', 'wt-1', true, undefined, 'Add safety relay')
  })

  it('dismisses the result section', async () => {
    vi.spyOn(api, 'compareMasterWithTia').mockResolvedValue(comparison())
    const { host } = await render({ signal: 1 })
    expect(host.querySelector('[data-testid="vc-compare-result"]')).toBeTruthy()

    await click(host.querySelector('button[aria-label="Dismiss comparison"]')!)
    expect(host.querySelector('[data-testid="vc-compare-result"]')).toBeNull()
  })
})
