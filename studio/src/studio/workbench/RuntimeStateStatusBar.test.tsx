// @vitest-environment happy-dom
import React, { act } from 'react'
import { createRoot } from 'react-dom/client'
import { afterEach, describe, expect, it } from 'vitest'
import RuntimeStateStatusBar from './RuntimeStateStatusBar'
import type { AppAssistantRuntimeSnapshot } from '@/api/client'

globalThis.IS_REACT_ACT_ENVIRONMENT = true

const runtime: AppAssistantRuntimeSnapshot = {
  schemaVersion: 1,
  workbenchId: 'wb-1',
  workbenchRevision: 12,
  focus: { worktreeId: 'wt-1', deviceId: 'plc-1' },
  worktrees: [{
    worktreeId: 'wt-1',
    name: 'master',
    branch: 'master',
    gitStatus: 'dirty',
    head: 'abc123456789',
    todoCount: 3,
    svnBaseRevision: 17,
    svnCurrentRevision: 20,
    validationState: 'success',
    devices: [{ deviceId: 'plc-1', plcName: 'PLC_1', tiaState: 'connected', knowledgeFreshness: 'fresh' }],
  }],
  availableActions: [],
  operation: { status: 'running', operationId: 'op-1', kind: 'compile', message: 'Compiling' },
  observedAt: '2026-08-11T01:02:03Z',
}

const render = (element: React.ReactNode) => {
  const host = document.createElement('div')
  document.body.appendChild(host)
  const root = createRoot(host)
  act(() => root.render(element))
  return { host, root }
}

afterEach(() => { document.body.innerHTML = '' })

describe('RuntimeStateStatusBar', () => {
  it('shows the focused runtime snapshot as an inspectable status item', () => {
    const { host, root } = render(
      <RuntimeStateStatusBar runtime={runtime} />,
    )

    expect(host.querySelector('[data-runtime-state]')?.textContent).toContain('Runtime rev 12')
    expect(host.textContent).toContain('compile · running')
    expect(host.textContent).toContain('Git dirty')
    expect(host.textContent).toContain('SVN 17 → 20')
    expect(host.textContent).toContain('Validation success')
    expect(host.textContent).toContain('PLC_1 · TIA connected · Knowledge fresh')
    expect(host.textContent).toContain('abc1234')
    expect(host.querySelector('time')?.getAttribute('datetime')).toBe(runtime.observedAt)

    act(() => root.unmount())
  })

  it('reports when the shared runtime snapshot is not available', () => {
    const { host, root } = render(<RuntimeStateStatusBar runtime={null} />)

    expect(host.textContent).toContain('Runtime state unavailable')
    expect(host.querySelector('[data-runtime-state]')).toBeTruthy()

    act(() => root.unmount())
  })

  it('handles a legacy snapshot whose worktree has no device list', () => {
    const legacyRuntime = {
      ...runtime,
      worktrees: runtime.worktrees.map(({ devices: _devices, ...worktree }) => worktree),
    } as unknown as AppAssistantRuntimeSnapshot

    const { host, root } = render(<RuntimeStateStatusBar runtime={legacyRuntime} />)

    expect(host.textContent).toContain('Deviceunknown')
    expect(host.textContent).toContain('Runtime rev 12')

    act(() => root.unmount())
  })

  it('handles a partial snapshot with no operation object', () => {
    const partialRuntime = {
      ...runtime,
      operation: undefined,
    } as unknown as AppAssistantRuntimeSnapshot

    const { host, root } = render(<RuntimeStateStatusBar runtime={partialRuntime} />)

    expect(host.textContent).toContain('Runtime rev 12')
    expect(host.textContent).toContain('Operationunknown')

    act(() => root.unmount())
  })
})
