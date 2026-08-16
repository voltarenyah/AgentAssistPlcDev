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

  it('pushes selected local objects into TIA and shows per-object outcomes', async () => {
    vi.spyOn(api, 'compareMasterWithTia').mockResolvedValue(comparison())
    const push = vi.spyOn(api, 'pushSourcesToTia').mockResolvedValue({
      comparisonId: 'comparison-1',
      outcomes: [{ path: 'devices/PLC_1/source/Blocks/Main.xml', success: true, message: null }],
    })
    const { host } = await render()

    await click(host.querySelector('button[aria-label="Compare with TIA"]')!)
    await click(host.querySelector('input[type="checkbox"]')!)
    await click(host.querySelector('button[aria-label="Push selected local changes to TIA"]')!)

    expect(push).toHaveBeenCalledWith('wb-1', 'comparison-1', ['devices/PLC_1/source/Blocks/Main.xml'])
    expect(host.textContent).toContain('✓')
    expect(host.textContent).toContain('devices/PLC_1/source/Blocks/Main.xml')
  })

  it('offers an SVN savepoint when checksums drift without source differences', async () => {
    vi.spyOn(api, 'compareMasterWithTia').mockResolvedValue(comparison({
      differences: [],
      state: 'Consistent',
      liveChecksums: { 'dev-1': 'checksum-9' },
    }))
    const createSavepoint = vi.spyOn(api, 'createSvnSavepoint').mockResolvedValue({
      sha: 'feedface00',
      message: 'safety change',
      files: ['engineering-state/revision.json'],
    })
    const { host } = await render()

    await click(host.querySelector('button[aria-label="Compare with TIA"]')!)

    expect(host.textContent).toContain('TIA changed outside the tracked source')
    const messageInput = host.querySelector('input[aria-label="Savepoint message"]') as HTMLInputElement
    await act(async () => {
      const setter = Object.getOwnPropertyDescriptor(window.HTMLInputElement.prototype, 'value')!.set!
      setter.call(messageInput, 'safety change')
      messageInput.dispatchEvent(new Event('input', { bubbles: true }))
    })
    await click(host.querySelector('button[aria-label="Create SVN savepoint"]')!)

    expect(createSavepoint).toHaveBeenCalledWith('wb-1', 'wt-1', 'safety change')
    expect(host.textContent).toContain('SVN savepoint committed feedface')
  })

  it('reports no need to commit when checksums match and no differences exist', async () => {
    vi.spyOn(api, 'compareMasterWithTia').mockResolvedValue(comparison({
      differences: [],
      state: 'Consistent',
      liveChecksums: { 'dev-1': 'checksum-2' },
    }))
    const { host } = await render()

    await click(host.querySelector('button[aria-label="Compare with TIA"]')!)

    expect(host.textContent).toContain('no need to commit')
    expect(host.textContent).not.toContain('TIA changed outside the tracked source')
  })

  it('requires a message before accepting project hardware changes', async () => {
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
    const { host } = await render()

    await click(host.querySelector('button[aria-label="Compare with TIA"]')!)

    expect(host.textContent).toContain('Project hardware differs from TIA')
    expect(host.textContent).not.toContain('TIA matches master')
    const acceptButton = host.querySelector('button[aria-label="Accept TIA hardware configuration"]') as HTMLButtonElement
    expect(acceptButton.disabled).toBe(true)
    await input(host.querySelector('input[aria-label="Hardware commit message"]')!, 'Add safety relay')
    expect(acceptButton.disabled).toBe(false)
    await click(acceptButton)

    expect(overwrite).toHaveBeenCalledWith('wb-1', 'wt-1', true, undefined, 'Add safety relay')
    expect(host.textContent).toContain('Hardware committed hardware')
  })
})
