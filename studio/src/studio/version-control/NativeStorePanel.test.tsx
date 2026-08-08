// @vitest-environment happy-dom
import React, { act } from 'react'
import { createRoot } from 'react-dom/client'
import { afterEach, describe, expect, it, vi } from 'vitest'
import * as api from '@/api/client'
import NativeStorePanel from './NativeStorePanel'

globalThis.IS_REACT_ACT_ENVIRONMENT = true

const state = (overrides: Partial<api.WorktreeEngineeringState> = {}): api.WorktreeEngineeringState => ({
  revision: {
    schemaVersion: 1,
    svn: { url: '^/native/main', revision: 7 },
    tia: { projectChecksum: 'PLC_1:AA BB' },
    safety: { fSignature: null },
    validation: { compileStatus: 'SUCCESS' },
  },
  svnUrl: null,
  baseSvnRevision: null,
  managedTiaProjectPath: 'C:/wb/worktrees/master/tia/tia.ap17',
  tiaStorePath: 'C:/wb/worktrees/master/tia',
  pendingCommit: false,
  ...overrides,
})

const savepoints: api.SavepointInfo[] = [
  { sha: 'abc123def', message: 'change A', svnUrl: '^/native/main', svnRevision: 7, projectChecksum: 'PLC_1:AA BB', compileStatus: 'SUCCESS', fSignature: null },
  { sha: '000999eee', message: 'baseline', svnUrl: '^/native/main', svnRevision: 2, projectChecksum: 'PLC_1:00 11', compileStatus: 'SUCCESS', fSignature: null },
]

const render = async (snapshot: api.WorktreeEngineeringState, points = savepoints) => {
  vi.spyOn(api, 'getWorktreeEngineeringState').mockResolvedValue(snapshot)
  vi.spyOn(api, 'getWorktreeSavepoints').mockResolvedValue(points)
  const host = document.createElement('div')
  document.body.appendChild(host)
  const root = createRoot(host)
  await act(async () => {
    root.render(<NativeStorePanel workbenchId="wb-1" worktreeId="wt-1" />)
  })
  return { host, root }
}

const click = async (element: Element) => {
  await act(async () => element.dispatchEvent(new MouseEvent('click', { bubbles: true })))
}

afterEach(() => {
  document.body.innerHTML = ''
  vi.restoreAllMocks()
})

describe('NativeStorePanel', () => {
  it('renders the revision.json fields', async () => {
    const { host } = await render(state())

    expect(host.textContent).toContain('^/native/main')
    expect(host.textContent).toContain('PLC_1:AA BB')
    expect(host.textContent).toContain('SUCCESS')
    expect(host.textContent).toContain('C:/wb/worktrees/master/tia/tia.ap17')
  })

  it('shows the pending-commit banner only when a pending record exists', async () => {
    const pending = await render(state({ pendingCommit: true }))
    expect(pending.host.textContent).toContain('PENDING_GIT_COMMIT')

    document.body.innerHTML = ''
    const clean = await render(state({ pendingCommit: false }))
    expect(clean.host.textContent).not.toContain('PENDING_GIT_COMMIT')
  })

  it('lists savepoints as revision + checksum + commit and restores the selection', async () => {
    const restore = vi.spyOn(api, 'restoreTiaProject').mockResolvedValue({
      gitCommit: '000999eee',
      svnUrl: '^/native/main',
      svnRevision: 2,
      restoredDirectory: 'C:/wb/export/PLC_1-0011',
      restoredProjectPath: 'C:/wb/export/PLC_1-0011/tia.ap17',
    })
    const { host } = await render(state())

    const select = host.querySelector('select')!
    const options = Array.from(select.querySelectorAll('option'))
    expect(options).toHaveLength(2)
    expect(options[0].textContent).toContain('r7')
    expect(options[0].textContent).toContain('PLC_1:AA BB')
    expect(options[0].textContent).toContain('abc123d')
    expect(options[1].textContent).toContain('r2')

    // Choose the older savepoint, then restore.
    await act(async () => {
      const setter = Object.getOwnPropertyDescriptor(window.HTMLSelectElement.prototype, 'value')!.set!
      setter.call(select, '000999eee')
      select.dispatchEvent(new Event('change', { bubbles: true }))
    })
    const button = Array.from(host.querySelectorAll('button')).find(candidate => candidate.textContent?.includes('Restore from SVN'))!
    await click(button)

    expect(restore).toHaveBeenCalledWith('wb-1', 'wt-1', '000999eee')
    expect(host.textContent).toContain('C:/wb/export/PLC_1-0011/tia.ap17')
    expect(host.textContent).toContain('^/native/main@2')
  })
})
