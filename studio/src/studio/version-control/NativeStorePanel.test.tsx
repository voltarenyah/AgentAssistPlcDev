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

const render = async (snapshot: api.WorktreeEngineeringState) => {
  vi.spyOn(api, 'getWorktreeEngineeringState').mockResolvedValue(snapshot)
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

const setInput = async (element: HTMLInputElement, value: string) => {
  const setter = Object.getOwnPropertyDescriptor(window.HTMLInputElement.prototype, 'value')!.set!
  await act(async () => {
    setter.call(element, value)
    element.dispatchEvent(new Event('input', { bubbles: true }))
  })
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

  it('restores via the api and shows the restored project path', async () => {
    const restore = vi.spyOn(api, 'restoreTiaProject').mockResolvedValue({
      gitCommit: 'abc123',
      svnUrl: '^/native/main',
      svnRevision: 5,
      restoredDirectory: 'C:/restore-target',
      restoredProjectPath: 'C:/restore-target/tia.ap17',
    })
    const { host } = await render(state())

    const inputs = host.querySelectorAll('input')
    const button = Array.from(host.querySelectorAll('button')).find(candidate => candidate.textContent?.includes('Restore from SVN'))!
    expect((button as HTMLButtonElement).disabled).toBe(true)

    await setInput(inputs[0] as HTMLInputElement, 'C:/restore-target')
    await setInput(inputs[1] as HTMLInputElement, 'abc123')
    await click(button)

    expect(restore).toHaveBeenCalledWith('wb-1', 'wt-1', 'C:/restore-target', 'abc123')
    expect(host.textContent).toContain('C:/restore-target/tia.ap17')
    expect(host.textContent).toContain('^/native/main@5')
  })
})
