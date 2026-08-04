// @vitest-environment happy-dom
import React, { act } from 'react'
import { createRoot } from 'react-dom/client'
import { afterEach, describe, expect, it, vi } from 'vitest'
import * as api from '@/api/client'
import VersionControlHistory from './VersionControlHistory'

globalThis.IS_REACT_ACT_ENVIRONMENT = true

const commit = (overrides: Partial<api.VcCommitEntry> = {}): api.VcCommitEntry => ({
  sha: 'abcdef1234567890',
  author: 'Ansel',
  message: 'Validate Main block',
  timestamp: '2026-08-04T08:00:00.000Z',
  files: ['devices/PLC_1/source/Blocks/Main.xml'],
  validationState: 'Validated',
  evidenceKind: 'feature-merge',
  ...overrides,
})

const render = async (element: React.ReactNode) => {
  const host = document.createElement('div')
  document.body.appendChild(host)
  const root = createRoot(host)
  await act(async () => root.render(element))
  return { host, root }
}

afterEach(() => {
  vi.restoreAllMocks()
  document.body.innerHTML = ''
})

describe('VersionControlHistory', () => {
  it('shows changed PLC objects and validation state for each commit', async () => {
    const { host } = await render(
      <VersionControlHistory
        workbenchId="wb-1"
        worktreeId="master"
        commits={[commit()]}
      />,
    )

    expect(host.textContent).toContain('Main')
    expect(host.textContent).toContain('TIA validated')
    expect(host.textContent).toContain('Feature merge')
  })

  it('loads permanent evidence when a validated commit is selected', async () => {
    vi.spyOn(api, 'getVcValidation').mockResolvedValue({
      schemaVersion: '1',
      evidenceKind: 'feature-merge',
      commitSha: 'abcdef1234567890',
      workbenchId: 'wb-1',
      sourceWorktreeId: 'feature-a',
      confirmedAt: '2026-08-04T08:01:00.000Z',
      confirmedBy: 'Ansel',
      machineValidated: true,
      devices: [{
        deviceId: 'dev-1',
        plcName: 'PLC_1',
        projectIdentity: 'project-1',
        projectChecksum: 'checksum-1',
        objects: [{
          identity: 'Main',
          relativePath: 'devices/PLC_1/source/Blocks/Main.xml',
          sha256: 'fingerprint-1',
        }],
      }],
    })

    const { host } = await render(
      <VersionControlHistory
        workbenchId="wb-1"
        worktreeId="master"
        commits={[commit()]}
      />,
    )

    await act(async () => {
      host.querySelector<HTMLButtonElement>('[data-testid="commit-abcdef1"]')?.click()
    })

    expect(host.textContent).toContain('Permanent evidence')
    expect(host.textContent).toContain('PLC_1')
    expect(host.textContent).toContain('checksum-1')
    expect(api.getVcValidation).toHaveBeenCalledWith('wb-1', 'master', 'abcdef1234567890')
  })

  it('creates a rollback feature instead of restoring master', async () => {
    const create = vi.spyOn(api, 'createRollbackFeature').mockResolvedValue({
      worktreeId: 'rollback-main',
      name: 'rollback-main',
    })
    const { host } = await render(
      <VersionControlHistory
        workbenchId="wb-1"
        worktreeId="master"
        commits={[commit()]}
      />,
    )

    await act(async () => {
      host.querySelector<HTMLButtonElement>('[data-testid="object-Main"]')?.click()
    })

    await act(async () => {
      host.querySelector<HTMLButtonElement>('[data-testid="create-rollback-feature"]')?.click()
    })

    expect(create).toHaveBeenCalledWith(
      'wb-1',
      'abcdef1234567890',
      ['devices/PLC_1/source/Blocks/Main.xml'],
      expect.any(String),
    )
    expect(host.textContent).toContain('Rollback feature created')
    expect(host.textContent).not.toContain('Reset master')
  })
})
