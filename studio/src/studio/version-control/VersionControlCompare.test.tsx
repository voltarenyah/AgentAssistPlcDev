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

const render = async (props: { signal?: number; commitMessage?: string; branch?: string; onCommitted?: () => void } = {}) => {
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

  it('accepts selected TIA changes using the changes-page commit message', async () => {
    const compare = vi.spyOn(api, 'compareMasterWithTia')
      .mockResolvedValueOnce(comparison())
      .mockResolvedValueOnce(comparison({ differences: [], state: 'Consistent' }))
    const accept = vi.spyOn(api, 'acceptTiaSynchronization').mockResolvedValue({
      comparisonId: 'comparison-1',
      pendingPaths: [],
      commitSha: 'commit-2',
    })
    const { host } = await render({ signal: 1, commitMessage: 'Accept Main from TIA' })

    await click(host.querySelector('input[type="checkbox"]')!)
    await click(host.querySelector('button[aria-label="Accept selected TIA changes"]')!)

    expect(accept).toHaveBeenCalledWith(
      'wb-1',
      'comparison-1',
      ['devices/PLC_1/source/Blocks/Main.xml'],
      'Accept Main from TIA',
    )
    expect(compare).toHaveBeenCalledTimes(2)
    expect(host.textContent).toContain('TIA matches master')
  })

  it('keeps accept disabled until a commit message exists', async () => {
    vi.spyOn(api, 'compareMasterWithTia').mockResolvedValue(comparison())
    const { host } = await render({ signal: 1, commitMessage: '' })

    await click(host.querySelector('input[type="checkbox"]')!)

    const acceptButton = host.querySelector('button[aria-label="Accept selected TIA changes"]') as HTMLButtonElement
    expect(acceptButton.disabled).toBe(true)
    expect(host.textContent).toContain('Type a commit message above')
  })

  it('pushes selected local objects into TIA and shows per-object outcomes', async () => {
    vi.spyOn(api, 'compareMasterWithTia').mockResolvedValue(comparison())
    const push = vi.spyOn(api, 'pushSourcesToTia').mockResolvedValue({
      comparisonId: 'comparison-1',
      outcomes: [{ path: 'devices/PLC_1/source/Blocks/Main.xml', success: true, message: null }],
    })
    const { host } = await render({ signal: 1 })

    await click(host.querySelector('input[type="checkbox"]')!)
    await click(host.querySelector('button[aria-label="Push selected local changes to TIA"]')!)

    expect(push).toHaveBeenCalledWith('wb-1', 'comparison-1', ['devices/PLC_1/source/Blocks/Main.xml'])
    expect(host.textContent).toContain('✓')
    expect(host.textContent).toContain('devices/PLC_1/source/Blocks/Main.xml')
  })

  it('points checksum drift without source differences to the snapshot area', async () => {
    vi.spyOn(api, 'compareMasterWithTia').mockResolvedValue(comparison({
      differences: [],
      state: 'Consistent',
      liveChecksums: { 'dev-1': 'checksum-9' },
    }))
    const { host } = await render({ signal: 1 })

    expect(host.textContent).toContain('TIA changed outside the tracked source')
    expect(host.textContent).toContain('TIA snapshot below')
  })

  it('reports TIA matches master when checksums match and no differences exist', async () => {
    vi.spyOn(api, 'compareMasterWithTia').mockResolvedValue(comparison({
      differences: [],
      state: 'Consistent',
      liveChecksums: { 'dev-1': 'checksum-2' },
    }))
    const { host } = await render({ signal: 1 })

    expect(host.textContent).toContain('TIA matches master')
    expect(host.textContent).not.toContain('TIA changed outside the tracked source')
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
